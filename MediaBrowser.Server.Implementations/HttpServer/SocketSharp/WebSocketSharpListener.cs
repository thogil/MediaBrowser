﻿using System.Collections.Specialized;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Server.Implementations.Logging;
using ServiceStack;
using ServiceStack.Web;
using SocketHttpListener.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MediaBrowser.Server.Implementations.HttpServer.SocketSharp
{
    public class WebSocketSharpListener : IHttpListener
    {
        private HttpListener _listener;

        private readonly ILogger _logger;
        private readonly Action<string> _endpointListener;
        private readonly string _certificatePath;

        public WebSocketSharpListener(ILogger logger, Action<string> endpointListener,
            string certificatePath)
        {
            _logger = logger;
            _endpointListener = endpointListener;
            _certificatePath = certificatePath;
        }

        public Action<Exception, IRequest> ErrorHandler { get; set; }

        public Func<IHttpRequest, Uri, Task> RequestHandler { get; set; }

        public Action<WebSocketConnectingEventArgs> WebSocketConnecting { get; set; }

        public Action<WebSocketConnectEventArgs> WebSocketConnected { get; set; }

        public void Start(IEnumerable<string> urlPrefixes)
        {
            if (_listener == null)
                _listener = new HttpListener(new PatternsLogger(_logger), _certificatePath);

            foreach (var prefix in urlPrefixes)
            {
                _logger.Info("Adding HttpListener prefix " + prefix);
                _listener.Prefixes.Add(prefix);
            }

            _listener.OnContext = ProcessContext;

            _listener.Start();
        }

        private void ProcessContext(HttpListenerContext context)
        {
            Task.Factory.StartNew(() => InitTask(context));
        }

        private void InitTask(HttpListenerContext context)
        {
            try
            {
                var task = this.ProcessRequestAsync(context);
                task.ContinueWith(x => HandleError(x.Exception, context), TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.AttachedToParent);

                //if (task.Status == TaskStatus.Created)
                //{
                //    task.RunSynchronously();
                //}
            }
            catch (Exception ex)
            {
                HandleError(ex, context);
            }
        }

        private Task ProcessRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;

            LogHttpRequest(request);

            if (request.IsWebSocketRequest)
            {
                ProcessWebSocketRequest(context);
                return Task.FromResult(true);
            }

            if (string.IsNullOrEmpty(context.Request.RawUrl))
                return ((object)null).AsTaskResult();

            var httpReq = GetRequest(context);

            return RequestHandler(httpReq, request.Url);
        }

        /// <summary>
        /// Logs the HTTP request.
        /// </summary>
        /// <param name="request">The request.</param>
        private void LogHttpRequest(HttpListenerRequest request)
        {
            var endpoint = request.LocalEndPoint;

            if (endpoint != null)
            {
                var address = endpoint.ToString();

                _endpointListener(address);
            }

            LogRequest(_logger, request);
        }

        private void ProcessWebSocketRequest(HttpListenerContext ctx)
        {
            try
            {
                var endpoint = ctx.Request.RemoteEndPoint.ToString();
                var url = ctx.Request.RawUrl;
                var queryString = new NameValueCollection(ctx.Request.QueryString ?? new NameValueCollection());

                var connectingArgs = new WebSocketConnectingEventArgs
                {
                    Url = url,
                    QueryString = queryString,
                    Endpoint = endpoint
                };

                if (WebSocketConnecting != null)
                {
                    WebSocketConnecting(connectingArgs);
                }

                if (connectingArgs.AllowConnection)
                {
                    _logger.Debug("Web socket connection allowed");

                    var webSocketContext = ctx.AcceptWebSocket(null);

                    if (WebSocketConnected != null)
                    {
                        WebSocketConnected(new WebSocketConnectEventArgs
                        {
                            Url = url,
                            QueryString = queryString,
                            WebSocket = new SharpWebSocket(webSocketContext.WebSocket, _logger),
                            Endpoint = endpoint
                        });
                    }
                }
                else
                {
                    _logger.Warn("Web socket connection not allowed");
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("AcceptWebSocketAsync error", ex);
                ctx.Response.StatusCode = 500;
                ctx.Response.Close();
            }
        }

        private IHttpRequest GetRequest(HttpListenerContext httpContext)
        {
            var operationName = httpContext.Request.GetOperationName();

            var req = new WebSocketSharpRequest(httpContext, operationName, RequestAttributes.None, _logger);
            req.RequestAttributes = req.GetAttributes();

            return req;
        }

        /// <summary>
        /// Logs the request.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="request">The request.</param>
        private static void LogRequest(ILogger logger, HttpListenerRequest request)
        {
            var log = new StringBuilder();

            var headers = string.Join(",", request.Headers.AllKeys.Where(i => !string.Equals(i, "cookie", StringComparison.OrdinalIgnoreCase) && !string.Equals(i, "Referer", StringComparison.OrdinalIgnoreCase)).Select(k => k + "=" + request.Headers[k]));

            log.AppendLine("Ip: " + request.RemoteEndPoint + ". Headers: " + headers);

            var type = request.IsWebSocketRequest ? "Web Socket" : "HTTP " + request.HttpMethod;

            logger.LogMultiline(type + " " + request.Url, LogSeverity.Info, log);
        }

        private void HandleError(Exception ex, HttpListenerContext context)
        {
            var httpReq = GetRequest(context);

            if (ErrorHandler != null)
            {
                ErrorHandler(ex, httpReq);
            }
        }

        public void Stop()
        {
            if (_listener != null)
            {
                foreach (var prefix in _listener.Prefixes.ToList())
                {
                    _listener.Prefixes.Remove(prefix);
                }

                _listener.Close();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        private bool _disposed;
        private readonly object _disposeLock = new object();
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            lock (_disposeLock)
            {
                if (_disposed) return;

                if (disposing)
                {
                    Stop();
                }

                //release unmanaged resources here...
                _disposed = true;
            }
        }
    }
}