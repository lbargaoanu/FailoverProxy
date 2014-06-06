using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Teamnet.FailoverProxy
{
    public sealed class Listener : IDisposable
    {
        private static readonly byte[] EmptyData = new byte[0];
        private static readonly int ConnectRetryTimeout = 1000 * Utils.GetSetting<int>("connectRetryTimeoutInSeconds", 2);
        private static readonly int DefaultReadSize = Utils.GetSetting<int>("defaultReadSizeInBytes", 256);
        private static readonly int ReadTimeout = 1000 * Utils.GetSetting<int>("readTimeoutInSeconds", 3);
        private static readonly IPEndPoint PrimaryDataEndpoint = Utils.GetEndpoint("primarySource");
        private static readonly IPEndPoint SecondaryDataEndpoint = Utils.GetEndpoint("secondarySource");
        private static readonly int DataPort = Utils.GetSetting<int>("dataPort");
        private const int ControlPortOffset = 2;
        private static readonly int ControlPort = DataPort + ControlPortOffset;
        private static readonly IPEndPoint PrimaryControlEndpoint = new IPEndPoint(PrimaryDataEndpoint.Address, PrimaryDataEndpoint.Port + ControlPortOffset);

        private ConcurrentBag<NetworkStream> dataClients = new ConcurrentBag<NetworkStream>();
        private volatile bool disposed;
        private byte[] primaryData = new byte[DefaultReadSize];
        private byte[] secondaryData = new byte[DefaultReadSize];

        private TcpListener dataListener;
        private TcpListener controlListener;
        private TcpClient primary;
        private TcpClient secondary;
        private TcpClient primaryControl;

        public async Task StartAsync()
        {
            while(!disposed)
            {
                Task acceptDataClients = null, forwardIncomingDataMessages = null, handleControlClients = null;
                try
                {
                    dataListener = new TcpListener(IPAddress.Any, DataPort);
                    dataListener.Start();
                    controlListener = new TcpListener(IPAddress.Any, ControlPort);
                    controlListener.Start();
                    
                    primary = new TcpClient();
                    secondary = new TcpClient();
                    
                    await ConnectAsync(primary, PrimaryDataEndpoint, secondary, SecondaryDataEndpoint);

                    acceptDataClients = AcceptDataClients();
                    handleControlClients = HandleControlClients();
                    forwardIncomingDataMessages = ForwardIncomingDataMessages();
                    
                    await await Task.WhenAny(handleControlClients, acceptDataClients, forwardIncomingDataMessages);
                }
                catch(Exception ex)
                {
                    Trace.WriteLine(ex);
                    if(!disposed)
                    {
                        DisposeCore();
                    }
                }
                await EnsureCompleted(acceptDataClients, forwardIncomingDataMessages, handleControlClients);
                if(!disposed)
                {
                    await Task.Delay(ConnectRetryTimeout);
                }
            }
        }

        private async Task EnsureCompleted(params Task[] tasks)
        {
            if(tasks.Contains(null))
            {
                return;
            }
            try
            {
                await Task.WhenAll(tasks);
            }
            catch(Exception ex)
            {
                Trace.WriteLine(ex);
            }
        }

        private async Task ConnectAsync(TcpClient primary, IPEndPoint primaryEndpoint, TcpClient secondary, IPEndPoint secondaryEndpoint)
        {
            var primaryConnect = primary.ConnectAsync(primaryEndpoint);
            var secondaryConnect = secondary.ConnectAsync(secondaryEndpoint);
            try
            {
                await Task.WhenAll(primaryConnect, secondaryConnect).ConfigureAwait(false);
            }
            catch(Exception ex)
            {
                if(primaryConnect.IsFaulted && secondaryConnect.IsFaulted)
                {
                    throw;
                }
                Trace.WriteLine(ex);
            }
            finally
            {
                TraceConnect(primaryConnect, primaryEndpoint);
                TraceConnect(secondaryConnect, secondaryEndpoint);
            }
        }

        private static void TraceConnect(Task connect, IPEndPoint endpoint)
        {
            if(connect.IsFaulted)
            {
                Trace.WriteLine(connect.Exception);
            }
            else
            {
                Trace.WriteLine("Connected to " + endpoint);
            }
        }

        private async Task ForwardIncomingDataMessages()
        {
            using(Stream primaryDataStream = GetStream(primary), secondaryDataStream = GetStream(secondary))
            {
                var primaryDataRead = primaryDataStream.ReadAsync(primaryData);
                var secondaryDataRead = secondaryDataStream.ReadAsync(secondaryData);
                while(true)
                {
                    var timeout = Task.Delay(ReadTimeout);
                    await await Task.WhenAny(primaryDataRead, secondaryDataRead, timeout);
                    if(timeout.IsCompleted)
                    {
                        Trace.WriteLine("Read timeout " + ReadTimeout);
                        await Task.WhenAll(primaryDataStream.WriteAsync(EmptyData), secondaryDataStream.WriteAsync(EmptyData));
                    }
                    else
                    {
                        primaryDataRead = await CheckSource(primary, primaryData, primaryDataStream, primaryDataRead);
                        secondaryDataRead = await CheckSource(secondary, secondaryData, secondaryDataStream, secondaryDataRead);
                    }
                }
            }
        }

        private async Task<Task<int>> CheckSource(TcpClient source, byte[] data, Stream stream, Task<int> read)
        {
            if(read.IsCompleted)
            {
                TraceReceive(source, read.Result);
                if(read.Result == 0)
                {
                    var connectionClosedTask = new TaskCompletionSource<int>();
                    connectionClosedTask.SetException(new Exception("Connection closed "+source.Client.RemoteEndPoint));
                    return connectionClosedTask.Task;
                }
                await SendDataToClients(data, read);
                read = stream.ReadAsync(data);
            }
            return read;
        }

        private static void TraceReceive(TcpClient source, int count)
        {
            Trace.WriteLine("Received " + count + " bytes from " + source.Client.RemoteEndPoint);
        }

        private Stream GetStream(TcpClient source)
        {
            if(source.Connected)
            {
                return source.GetStream();
            }
            return new NullStream();
        }

        private async Task SendDataToClients(byte[] data, Task<int> readTask)
        {
            var sends = dataClients.Select(dataClient =>
            {
                Trace.WriteLine("Send data");
                return dataClient.WriteAsync(data, 0, readTask.Result);
            });
            await Task.WhenAll(sends);
        }

        private async Task AcceptDataClients()
        {
            while(true)
            {
                var dataClient = await dataListener.AcceptSocketAsync(); 
                dataClients.Add(new NetworkStream(dataClient, ownsSocket: true));
            }
        }

        private async Task HandleControlClients()
        {
            var controlTasks = new List<Task>();
            var acceptControlClientTask = AcceptControlClient(controlTasks);
            while(controlTasks.Count > 0)
            {
                var completed = await Task.WhenAny(controlTasks);
                controlTasks.Remove(completed);
                try
                {
                    await completed;
                }
                catch(Exception ex)
                {
                    Trace.WriteLine(ex);
                }
                Trace.WriteLine("Pending control connections : " + controlTasks.Count);
                if(controlListener.Server.IsBound && acceptControlClientTask.IsCompleted)
                {
                    if(acceptControlClientTask.Status == TaskStatus.RanToCompletion)
                    {
                        var controlTask = HandleControlConnection(acceptControlClientTask.Result);
                        controlTasks.Add(controlTask);
                    }
                    acceptControlClientTask = AcceptControlClient(controlTasks);
                }
            }
        }

        private Task<Socket> AcceptControlClient(List<Task> controlTasks)
        {
            var acceptControlClientTask = controlListener.AcceptSocketAsync();
            controlTasks.Add(acceptControlClientTask);
            return acceptControlClientTask;
        }

        private async Task HandleControlConnection(Socket controlClientSocket)
        {
            Trace.WriteLine("New control client " + controlClientSocket.RemoteEndPoint);
            using(var controlClientStream = new NetworkStream(controlClientSocket, ownsSocket: true))
            {
                using(primaryControl = new TcpClient())
                {
                    await primaryControl.ConnectAsync(PrimaryControlEndpoint);
                    Trace.WriteLine("Connected to control " + PrimaryControlEndpoint);
                    using(var primaryControlStream = primaryControl.GetStream())
                    {
                        Action<Task> close = task =>
                        {
                            if(task.IsFaulted)
                            {
                                Trace.WriteLine(task.Exception);
                            }
                            TryCatch(primaryControl.Close);
                            TryCatch(controlClientSocket.Dispose);
                        };
                        var forwardCommands = controlClientStream.CopyToAsync(primaryControlStream, DefaultReadSize).ContinueWith(close, TaskContinuationOptions.ExecuteSynchronously);
                        var forwardResponses = primaryControlStream.CopyToAsync(controlClientStream, DefaultReadSize).ContinueWith(close, TaskContinuationOptions.ExecuteSynchronously);
                        await Task.WhenAll(forwardCommands, forwardResponses);
                    }
                }
                primaryControl = null;
            }
        }

        public void Dispose()
        {
            disposed = true;
            DisposeCore();
        }

        private void DisposeCore()
        {
            CloseListeners();
            CloseAllClients();
            CloseSources();
        }

        private void CloseListeners()
        {
            TryCatchStop(dataListener);
            TryCatchStop(controlListener);
        }

        private void CloseAllClients()
        {
            NetworkStream client;
            while(dataClients.TryTake(out client))
            {
                TryCatch(client.Close);
            }
        }

        private void CloseSources()
        {
            TryCatchDispose(primary);
            TryCatchDispose(primaryControl);
            TryCatchDispose(secondary);
        }

        private static void TryCatchStop(TcpListener listener)
        {
            if(listener != null)
            {
                TryCatch(listener.Stop);
            }
        }

        private static void TryCatchDispose(IDisposable disposable)
        {
            if(disposable != null)
            {
                TryCatch(disposable.Dispose);
            }
        }

        private static void TryCatch(Action tryAction, Action<Exception> catchAction = null)
        {
            try
            {
                tryAction();
            }
            catch(Exception ex)
            {
                Trace.WriteLine(ex);
                if(catchAction != null)
                {
                    catchAction(ex);
                }
            }
        }
    }
}