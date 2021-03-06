﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http.Controllers;
using System.Web.Http.ExceptionHandling;
using System.Web.Http.Hosting;
using System.Web.Http.Owin.ExceptionHandling;
using System.Web.Http.Owin.Properties;
using Microsoft.Owin;

namespace System.Web.Http.Owin
{
    /// <summary>
    /// Represents an OWIN component that submits requests to an <see cref="HttpMessageHandler"/> when invoked.
    /// </summary>
    public class HttpMessageHandlerAdapter : OwinMiddleware, IDisposable
    {
        private readonly HttpMessageHandler _messageHandler;
        private readonly HttpMessageInvoker _messageInvoker;
        private readonly IHostBufferPolicySelector _bufferPolicySelector;
        private readonly IExceptionLogger _exceptionLogger;
        private readonly IExceptionHandler _exceptionHandler;

        private bool _disposed;

        /// <summary>Initializes a new instance of the <see cref="HttpMessageHandlerAdapter"/> class.</summary>
        /// <param name="next">The next component in the pipeline.</param>
        /// <param name="options">The options to configure this adapter.</param>
        public HttpMessageHandlerAdapter(OwinMiddleware next, HttpMessageHandlerOptions options)
            : base(next)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }

            _messageHandler = options.MessageHandler;

            if (_messageHandler == null)
            {
                throw new ArgumentException(Error.Format(OwinResources.TypePropertyMustNotBeNull,
                    typeof(HttpMessageHandlerOptions).Name, "MessageHandler"), "options");
            }

            _messageInvoker = new HttpMessageInvoker(_messageHandler);
            _bufferPolicySelector = options.BufferPolicySelector;

            if (_bufferPolicySelector == null)
            {
                throw new ArgumentException(Error.Format(OwinResources.TypePropertyMustNotBeNull,
                    typeof(HttpMessageHandlerOptions).Name, "BufferPolicySelector"), "options");
            }

            _exceptionLogger = options.ExceptionLogger;

            if (_exceptionLogger == null)
            {
                throw new ArgumentException(Error.Format(OwinResources.TypePropertyMustNotBeNull,
                    typeof(HttpMessageHandlerOptions).Name, "ExceptionLogger"), "options");
            }

            _exceptionHandler = options.ExceptionHandler;

            if (_exceptionHandler == null)
            {
                throw new ArgumentException(Error.Format(OwinResources.TypePropertyMustNotBeNull,
                    typeof(HttpMessageHandlerOptions).Name, "ExceptionHandler"), "options");
            }
        }

        /// <summary>Initializes a new instance of the <see cref="HttpMessageHandlerAdapter"/> class.</summary>
        /// <param name="next">The next component in the pipeline.</param>
        /// <param name="messageHandler">The <see cref="HttpMessageHandler"/> to submit requests to.</param>
        /// <param name="bufferPolicySelector">
        /// The <see cref="IHostBufferPolicySelector"/> that determines whether or not to buffer requests and
        /// responses.
        /// </param>
        public HttpMessageHandlerAdapter(OwinMiddleware next, HttpMessageHandler messageHandler,
            IHostBufferPolicySelector bufferPolicySelector)
            : this(next, CreateOptions(messageHandler, bufferPolicySelector))
        {
        }

        /// <summary>Gets the <see cref="HttpMessageHandler"/> to submit requests to.</summary>
        public HttpMessageHandler MessageHandler
        {
            get { return _messageHandler; }
        }

        /// <summary>
        /// Gets the <see cref="IHostBufferPolicySelector"/> that determines whether or not to buffer requests and
        /// responses.
        /// </summary>
        public IHostBufferPolicySelector BufferPolicySelector
        {
            get { return _bufferPolicySelector; }
        }

        /// <summary>Gets the <see cref="IExceptionLogger"/> to use to log unhandled exceptions.</summary>
        public IExceptionLogger ExceptionLogger
        {
            get { return _exceptionLogger; }
        }

        /// <summary>Gets the <see cref="IExceptionHandler"/> to use to process unhandled exceptions.</summary>
        public IExceptionHandler ExceptionHandler
        {
            get { return _exceptionHandler; }
        }

        /// <inheritdoc />
        public override Task Invoke(IOwinContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            IOwinRequest owinRequest = context.Request;
            IOwinResponse owinResponse = context.Response;

            if (owinRequest == null)
            {
                throw Error.InvalidOperation(OwinResources.OwinContext_NullRequest);
            }
            if (owinResponse == null)
            {
                throw Error.InvalidOperation(OwinResources.OwinContext_NullResponse);
            }

            return InvokeCore(context, owinRequest, owinResponse);
        }

        private async Task InvokeCore(IOwinContext context, IOwinRequest owinRequest,
            IOwinResponse owinResponse)
        {
            CancellationToken cancellationToken = owinRequest.CallCancelled;
            HttpContent requestContent;

            if (!owinRequest.Body.CanSeek && _bufferPolicySelector.UseBufferedInputStream(hostContext: context))
            {
                requestContent = await CreateBufferedRequestContentAsync(owinRequest, cancellationToken);
            }
            else
            {
                requestContent = CreateStreamedRequestContent(owinRequest);
            }

            HttpRequestMessage request = CreateRequestMessage(owinRequest, requestContent);
            MapRequestProperties(request, context);

            SetPrincipal(owinRequest.User);

            HttpResponseMessage response = null;
            bool callNext;

            try
            {
                response = await _messageInvoker.SendAsync(request, cancellationToken);

                // Handle null responses
                if (response == null)
                {
                    throw Error.InvalidOperation(OwinResources.SendAsync_ReturnedNull);
                }

                // Handle soft 404s where no route matched - call the next component
                if (IsSoftNotFound(request, response))
                {
                    callNext = true;
                }
                else
                {
                    callNext = false;

                    if (response.Content != null && _bufferPolicySelector.UseBufferedOutputStream(response))
                    {
                        response = await BufferResponseContentAsync(request, response, cancellationToken);
                    }

                    FixUpContentLengthHeaders(response);
                    await SendResponseMessageAsync(request, response, owinResponse, cancellationToken);
                }
            }
            finally
            {
                request.DisposeRequestResources();
                request.Dispose();
                if (response != null)
                {
                    response.Dispose();
                }
            }

            // Call the next component if no route matched
            if (callNext && Next != null)
            {
                await Next.Invoke(context);
            }
        }

        private static HttpContent CreateStreamedRequestContent(IOwinRequest owinRequest)
        {
            // Note that we must NOT dispose owinRequest.Body in this case. Disposing it would close the input
            // stream and prevent cascaded components from accessing it. The server MUST handle any necessary
            // cleanup upon request completion. NonOwnedStream prevents StreamContent (or its callers including
            // HttpRequestMessage) from calling Close or Dispose on owinRequest.Body.
            return new StreamContent(new NonOwnedStream(owinRequest.Body));
        }

        private static async Task<HttpContent> CreateBufferedRequestContentAsync(IOwinRequest owinRequest,
            CancellationToken cancellationToken)
        {
            // We need to replace the request body with a buffered stream so that other components can read the stream.
            // For this stream to be useful, it must NOT be diposed along with the request. Streams created by
            // StreamContent do get disposed along with the request, so use MemoryStream to buffer separately.
            MemoryStream buffer = new MemoryStream();

            cancellationToken.ThrowIfCancellationRequested();

            using (StreamContent copier = new StreamContent(owinRequest.Body))
            {
                await copier.CopyToAsync(buffer);
            }

            // Provide the non-disposing, buffered stream to later OWIN components (set to the stream's beginning).
            buffer.Position = 0;
            owinRequest.Body = buffer;

            // For MemoryStream, Length is guaranteed to be an int.
            return new ByteArrayContent(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static HttpRequestMessage CreateRequestMessage(IOwinRequest owinRequest, HttpContent requestContent)
        {
            // Create the request
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(owinRequest.Method), owinRequest.Uri);

            try
            {
                // Set the body
                request.Content = requestContent;

                // Copy the headers
                foreach (KeyValuePair<string, string[]> header in owinRequest.Headers)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    {
                        bool success = requestContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        Contract.Assert(success,
                            "Every header can be added either to the request headers or to the content headers");
                    }
                }
            }
            catch
            {
                request.Dispose();
                throw;
            }

            return request;
        }

        private static void MapRequestProperties(HttpRequestMessage request, IOwinContext context)
        {
            // Set the OWIN context on the request
            request.SetOwinContext(context);

            // Set a request context on the request that lazily populates each property.
            HttpRequestContext requestContext = new OwinHttpRequestContext(context, request);
            request.SetRequestContext(requestContext);
        }

        private static void SetPrincipal(IPrincipal user)
        {
            if (user != null)
            {
                Thread.CurrentPrincipal = user;
            }
        }

        private static bool IsSoftNotFound(HttpRequestMessage request, HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                bool routingFailure;
                if (request.Properties.TryGetValue<bool>(HttpPropertyKeys.NoRouteMatched, out routingFailure)
                    && routingFailure)
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<HttpResponseMessage> BufferResponseContentAsync(HttpRequestMessage request,
            HttpResponseMessage response, CancellationToken cancellationToken)
        {
            ExceptionDispatchInfo exceptionInfo;

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await response.Content.LoadIntoBufferAsync();
                return response;
            }
            catch (Exception exception)
            {
                exceptionInfo = ExceptionDispatchInfo.Capture(exception);
            }

            // If the content can't be buffered, create a buffered error response for the exception
            // This code will commonly run when a formatter throws during the process of serialization

            Debug.Assert(exceptionInfo.SourceException != null);

            ExceptionContext exceptionContext = new ExceptionContext(exceptionInfo.SourceException,
                OwinExceptionCatchBlocks.HttpMessageHandlerAdapterBufferContent, request, response);

            await _exceptionLogger.LogAsync(exceptionContext, canBeHandled: true,
                cancellationToken: cancellationToken);
            HttpResponseMessage errorResponse = await _exceptionHandler.HandleAsync(exceptionContext,
                cancellationToken);

            response.Dispose();

            if (errorResponse == null)
            {
                exceptionInfo.Throw();
                return null;
            }

            // We have an error response to try to buffer and send back.

            response = errorResponse;
            cancellationToken.ThrowIfCancellationRequested();

            Exception errorException;

            try
            {
                // Try to buffer the error response and send it back.
                await response.Content.LoadIntoBufferAsync();
                return response;
            }
            catch (Exception exception)
            {
                errorException = exception;
            }

            // We tried to send back an error response with content, but we couldn't. It's an edge case; the best we
            // can do is to log that exception and send back an empty 500.

            ExceptionContext errorExceptionContext = new ExceptionContext(errorException,
                OwinExceptionCatchBlocks.HttpMessageHandlerAdapterBufferError, request, response);
            await _exceptionLogger.LogAsync(errorExceptionContext, canBeHandled: false,
                cancellationToken: cancellationToken);

            response.Dispose();
            return request.CreateResponse(HttpStatusCode.InternalServerError);
        }

        // Responsible for setting Content-Length and Transfer-Encoding if needed
        private static void FixUpContentLengthHeaders(HttpResponseMessage response)
        {
            HttpContent responseContent = response.Content;
            if (responseContent != null)
            {
                if (response.Headers.TransferEncodingChunked == true)
                {
                    // According to section 4.4 of the HTTP 1.1 spec, HTTP responses that use chunked transfer
                    // encoding must not have a content length set. Chunked should take precedence over content
                    // length in this case because chunked is always set explicitly by users while the Content-Length
                    // header can be added implicitly by System.Net.Http.
                    responseContent.Headers.ContentLength = null;
                }
                else
                {
                    // Triggers delayed content-length calculations.
                    if (responseContent.Headers.ContentLength == null)
                    {
                        // If there is no content-length we can compute, then the response should use
                        // chunked transfer encoding to prevent the server from buffering the content
                        response.Headers.TransferEncodingChunked = true;
                    }
                }
            }
        }

        private Task SendResponseMessageAsync(HttpRequestMessage request, HttpResponseMessage response,
            IOwinResponse owinResponse, CancellationToken cancellationToken)
        {
            owinResponse.StatusCode = (int)response.StatusCode;
            owinResponse.ReasonPhrase = response.ReasonPhrase;

            // Copy non-content headers
            IDictionary<string, string[]> responseHeaders = owinResponse.Headers;
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                responseHeaders[header.Key] = header.Value.AsArray();
            }

            HttpContent responseContent = response.Content;
            if (responseContent == null)
            {
                // Set the content-length to 0 to prevent the server from sending back the response chunked
                responseHeaders["Content-Length"] = new string[] { "0" };
                return TaskHelpers.Completed();
            }
            else
            {
                // Copy content headers
                foreach (KeyValuePair<string, IEnumerable<string>> contentHeader in responseContent.Headers)
                {
                    responseHeaders[contentHeader.Key] = contentHeader.Value.AsArray();
                }

                // Copy body
                return SendResponseContentAsync(request, response, owinResponse.Body, cancellationToken);
            }
        }

        private async Task SendResponseContentAsync(HttpRequestMessage request, HttpResponseMessage response,
            Stream body, CancellationToken cancellationToken)
        {
            Contract.Assert(response != null);
            Contract.Assert(response.Content != null);

            Exception exception;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await response.Content.CopyToAsync(body);
                return;
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            
            // We're streaming content, so we can only call loggers, not handlers, as we've already (possibly) send the
            // status code and headers across the wire. Log the exception, but then just abort.
            ExceptionContext exceptionContext = new ExceptionContext(exception,
                OwinExceptionCatchBlocks.HttpMessageHandlerAdapterStreamContent, request, response);
            await _exceptionLogger.LogAsync(exceptionContext, canBeHandled: false,
                cancellationToken: cancellationToken);
            AbortResponseStream(body);
        }

        private static void AbortResponseStream(Stream body)
        {
            // OWIN doesn't yet support an explicit Abort even. Calling Dispose on the body seems like the best we can
            // do for nowe.
            body.Dispose();
        }

        private static HttpMessageHandlerOptions CreateOptions(HttpMessageHandler messageHandler,
            IHostBufferPolicySelector bufferPolicySelector)
        {
            if (messageHandler == null)
            {
                throw new ArgumentNullException("messageHandler");
            }

            if (bufferPolicySelector == null)
            {
                throw new ArgumentNullException("bufferPolicySelector");
            }

            return new HttpMessageHandlerOptions
            {
                MessageHandler = messageHandler,
                BufferPolicySelector = bufferPolicySelector,
                ExceptionLogger = new EmptyExceptionLogger(),
                ExceptionHandler = new DefaultExceptionHandler()
            };
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources.
        /// </summary>
        /// <param name="disposing">
        /// <see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release
        /// only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _messageInvoker.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
