using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Watch_Face_Editor.McpBridge
{
    /// <summary>Error returned to the bridge client as { "ok": false, "error": { code, message } }.</summary>
    internal sealed class McpBridgeException : Exception
    {
        public string Code { get; private set; }

        public McpBridgeException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// Optional local bridge for an MCP server (off by default, see McpBridge/Form1.McpBridge.cs).
    /// Listens on a named pipe of the current Windows user and speaks one JSON line per request:
    ///   client -> { "id": 1, "cmd": "get_state", "args": { } }
    ///   server -> { "id": 1, "ok": true, "result": { } }
    /// A named pipe (not a TCP port) keeps the bridge off the network: the pipe only accepts the user
    /// who started the editor, remote (network) clients are denied, and it disappears with the process.
    /// </summary>
    internal sealed class McpBridgeServer : IDisposable
    {
        public const int ProtocolVersion = 1;
        public const string PipePrefix = "wfe-mcp-bridge-";

        private const int ReadTimeoutMs = 15000;
        private const int WriteTimeoutMs = 60000;

        private readonly string pipeName;
        private readonly Func<string, string> handleRequest; // request line -> response line (runs on the pipe thread)
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private Thread thread;

        public string PipeName { get { return pipeName; } }
        public bool IsRunning { get; private set; }
        public string LastError { get; private set; }

        public McpBridgeServer(string pipeName, Func<string, string> handleRequest)
        {
            this.pipeName = pipeName;
            this.handleRequest = handleRequest;
        }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Listen);
            thread.IsBackground = true; // never keeps the editor alive
            thread.Name = "WFE MCP bridge";
            thread.Start();
        }

        public void Dispose()
        {
            try { cts.Cancel(); }
            catch (Exception) { }
        }

        private NamedPipeServerStream CreatePipe()
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User;
            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 65536, 65536, security);
        }

        private void Listen()
        {
            CancellationToken token = cts.Token;
            NamedPipeServerStream server = null;
            int failures = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (server == null) server = CreatePipe();
                        IsRunning = true;
                        server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                        Serve(server, token);
                        failures = 0;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Broken connection or a client that went away: start again with a fresh pipe.
                        LastError = ex.Message;
                        Logger.WriteLine("MCP bridge: " + ex.Message);
                        if (server != null)
                        {
                            try { server.Dispose(); } catch (Exception) { }
                            server = null;
                        }
                        failures++;
                        if (failures > 20) break; // e.g. the pipe name is taken: give up instead of spinning
                        if (token.WaitHandle.WaitOne(200)) break;
                        continue;
                    }

                    // Always disconnect, also when the client already closed its end (the pipe is then "broken"
                    // and would refuse the next WaitForConnection). The same pipe instance is reused, so the
                    // pipe name never disappears between two requests.
                    try
                    {
                        server.Disconnect();
                    }
                    catch (Exception)
                    {
                        try { server.Dispose(); } catch (Exception) { }
                        server = null;
                    }
                }
            }
            finally
            {
                IsRunning = false;
                if (server != null)
                {
                    try { server.Dispose(); } catch (Exception) { }
                }
            }
        }

        private void Serve(NamedPipeServerStream server, CancellationToken token)
        {
            UTF8Encoding utf8 = new UTF8Encoding(false);
            using (StreamReader reader = new StreamReader(server, utf8, false, 65536, true))
            {
                Task<string> read = reader.ReadLineAsync();
                if (!read.Wait(ReadTimeoutMs, token)) return; // connected but silent: drop it
                string request = read.Result;
                if (string.IsNullOrWhiteSpace(request)) return;

                string response = handleRequest(request);
                if (response == null) response = "";
                byte[] bytes = utf8.GetBytes(response + "\n");
                Task write = server.WriteAsync(bytes, 0, bytes.Length, token);
                if (!write.Wait(WriteTimeoutMs, token)) return;
                server.Flush();

                // Disconnecting right away would throw away what the client has not read yet,
                // so wait until the client closes its end (it does that after reading the answer).
                Task<string> closed = reader.ReadLineAsync();
                try { closed.Wait(ReadTimeoutMs, token); }
                catch (AggregateException) { }
            }
        }
    }
}
