using System;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Teamnet.FailoverProxy
{
    public static class Utils
    {
        public static bool IsNullOrEmpty(this string value)
        {
            return string.IsNullOrEmpty(value);
        }

        public static Task WriteAsync(this Stream networkStream, byte[] data)
        {
            return networkStream.WriteAsync(data, 0, data.Length);
        }

        public static Task<int> ReadAsync(this Stream networkStream, byte[] data)
        {
            return networkStream.ReadAsync(data, 0, data.Length);
        }

        public static Task ConnectAsync(this TcpClient client, IPEndPoint endpoint)
        {
            return client.ConnectAsync(endpoint.Address, endpoint.Port);
        }

        public static IPEndPoint GetEndpoint(string key)
        {
            return ParseEndpoint(GetSetting(key));
        }

        public static IPEndPoint ParseEndpoint(string endpoint)
        {
            try
            {
                string[] fragments = endpoint.Trim().Split(':');
                string host = fragments[0];
                int port = int.Parse(fragments[1], CultureInfo.InvariantCulture);
                return new IPEndPoint(IPAddress.Parse(host), port);
            }
            catch(Exception ex)
            {
                throw new ArgumentException("The endpoint address should have the form HostName:PortNumber.", ex);
            }
        }

        public static string GetSetting(string key)
        {
            string value = ConfigurationManager.AppSettings[key];
            if(string.IsNullOrEmpty(value))
            {
                throw new ConfigurationErrorsException("Missing key in AppSettings: " + key);
            }
            return value;
        }

        public static string SafeToString(this object value)
        {
            return (value == null) ? string.Empty : value.ToString();
        }

        public static T GetSetting<T>(string key, T defaultValue)
        {
            if(ConfigurationManager.AppSettings[key] == null)
            {
                return defaultValue;
            }
            return GetSetting<T>(key);
        }

        public static T GetSetting<T>(string key)
        {
            return Parse<T>(GetSetting(key));
        }

        private static T Parse<T>(string value)
        {
            if(typeof(T).IsEnum)
            {
                return (T)Enum.Parse(typeof(T), value, false);
            }
            var convertible = (IConvertible)value;
            return (T)convertible.ToType(typeof(T), CultureInfo.InvariantCulture);
        }
    }

    public class NullStream : Stream
    {
        private static readonly Task CompletedTask = Task.FromResult(0);
        private readonly TaskCompletionSource<int> disposeCompletingSource = new TaskCompletionSource<int>();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
        {
            cancellationToken.Register(()=>disposeCompletingSource.TrySetCanceled());
            return disposeCompletingSource.Task;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
        {
            return CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if(disposing)
            {
                disposeCompletingSource.SetResult(0);
            }
            base.Dispose(disposing);
        }

        public override bool CanRead
        {
            get { throw new NotImplementedException(); }
        }

        public override bool CanSeek
        {
            get { throw new NotImplementedException(); }
        }

        public override bool CanWrite
        {
            get { throw new NotImplementedException(); }
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override long Length
        {
            get { throw new NotImplementedException(); }
        }

        public override long Position
        {
            get
            {
                throw new NotImplementedException();
            }
            set
            {
                throw new NotImplementedException();
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}