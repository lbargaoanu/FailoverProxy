using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Teamnet.FailoverProxy
{
    class Program
    {
        static void Main(string[] args)
        {
            Trace.Listeners.Add(new ConsoleTraceListener());
            using(var listener = new Listener())
            {
                listener.StartAsync().Wait();
            }
        }

        static async Task X()
        {
            //var task = Task.Run(() => { throw new Exception("&&&&&&&&&&&"); });
            try
            {
                var tcpClient = new TcpClient("127.0.0.1", 6500);
                var stream = tcpClient.GetStream();
                Console.WriteLine("Stop");
                Console.ReadLine();
                var write = stream.WriteAsync(new byte[0]);
                await Task.WhenAll(write);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
//var socket = new Socket(SocketType.Stream, ProtocolType.IP);
//socket.Connect("127.0.0.1", 8183);
//var data = new byte[256];
//var count = socket.Receive(data);
//var count = stream.Read(data, 0, data.Length);
//var task = stream.ReadAsync(data, 0, data.Length);
//task.Wait();
//X().Wait();
//return;