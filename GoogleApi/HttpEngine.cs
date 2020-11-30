using GoogleApi.Entities;
using GoogleApi.Entities.Common.Enums;
using GoogleApi.Entities.Interfaces;
using GoogleApi.Exceptions;
using Newtonsoft.Json;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GoogleApi
{
    /// <summary>
    /// Http Engine (abstract).
    /// </summary>
    public abstract class HttpEngine
    {
        private static HttpClient httpClient;
        private static readonly TimeSpan httpTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Http Client.
        /// </summary>
        protected internal static HttpClient HttpClient
        {
            get
            {
                if (HttpEngine.httpClient == null)
                {
                    var httpClientHandler = new HttpClientHandler
                    {
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };

                    if (HttpEngine.Proxy != null)
                    {
                        httpClientHandler.Proxy = HttpEngine.Proxy;
                    }

                    HttpEngine.httpClient = new HttpClient(httpClientHandler)
                    {
                        Timeout = HttpEngine.httpTimeout
                    };

                    HttpEngine.httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                }

                return HttpEngine.httpClient;
            }
            set
            {
                HttpEngine.httpClient = value;
            }
        }

        /// <summary>
        /// Proxy property that will be used for all requests.
        /// </summary>
        public static IWebProxy Proxy { get; set; }


    }

    /// <summary>
    /// Http Engine.
    /// Manges the http connections, and is responsible for invoking requst and handling responses.
    /// </summary>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResponse"></typeparam>
    public sealed class HttpEngine<TRequest, TResponse> : HttpEngine
        where TRequest : IRequest, new()
        where TResponse : IResponse, new()
    {
        internal static readonly HttpEngine<TRequest, TResponse> instance = new HttpEngine<TRequest, TResponse>();

        /// <summary>
        /// Query.
        /// </summary>
        /// <param name="request">The request that will be sent.</param>
        /// <returns>The <see cref="IResponse"/>.</returns>
        public async Task<TResponse> Query(TRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                var result = await this.ProcessRequest(request);
                var response = await this.ProcessResponse(result);

                switch (response.Status)
                {
                    case Status.Ok:
                    case Status.ZeroResults:
                        return response;

                    default:
                        throw new GoogleApiException($"{response.Status}: {response.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                if (ex is GoogleApiException)
                    throw;

                throw new GoogleApiException(ex.Message, ex);
            }
        }

        /// <summary>
        /// Query Async.
        /// </summary>
        /// <param name="request">The request that will be sent.</param>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/>.</param>
        /// <returns>The <see cref="Task{T}"/>.</returns>
        public async Task<TResponse> QueryAsync(TRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (cancellationToken == null)
                throw new ArgumentNullException(nameof(cancellationToken));

            var taskCompletion = new TaskCompletionSource<TResponse>();

            var result = await this.ProcessRequestAsync(request, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
			{
                return default;
			}
            try
            {
                var response = await this.ProcessResponseAsync(result);

                switch (response.Status)
                {
                    case Status.Ok:
                    case Status.ZeroResults:
                        taskCompletion.SetResult(response);
                        break;

                    default:
                        throw new GoogleApiException($"{response.Status}: {response.ErrorMessage}");
                }

            }
            catch (Exception ex)
            {
                if (ex is GoogleApiException)
                {
                    taskCompletion.SetException(ex);
                }
                else
                {
                    var baseException = ex.GetBaseException();
                    var exception = new GoogleApiException(baseException.Message, baseException);

                    taskCompletion.SetException(exception);
                }
            }

            return default;
        }

        private async Task<HttpResponseMessage> ProcessRequest(TRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var uri = request.GetUri();

            if (request is IRequestQueryString)
            {
                return await HttpEngine.HttpClient.GetAsync(uri);
            }

            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };
            var serializeObject = JsonConvert.SerializeObject(request, settings);

            using (var stringContent = new StringContent(serializeObject, Encoding.UTF8))
            {
                var content = await stringContent.ReadAsStreamAsync();

                using (var streamContent = new StreamContent(content))
                {
                    return await HttpEngine.HttpClient.PostAsync(uri, streamContent);
                }
            }
        }
        private async Task<HttpResponseMessage> ProcessRequestAsync(TRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var uri = request.GetUri();

            if (request is IRequestQueryString)
            {
                return await HttpEngine.HttpClient.GetAsync(uri, cancellationToken);
            }

            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };
            var serializeObject = JsonConvert.SerializeObject(request, settings);

            using (var stringContent = new StringContent(serializeObject, Encoding.UTF8))
            {
                var content = await stringContent.ReadAsStreamAsync();

                using (var streamContent = new StreamContent(content))
                {
                    return await HttpEngine.HttpClient.PostAsync(uri, streamContent, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        private async Task<TResponse> ProcessResponse(HttpResponseMessage httpResponse)
        {
            if (httpResponse == null)
                throw new ArgumentNullException(nameof(httpResponse));

            using (httpResponse)
            {
                httpResponse.EnsureSuccessStatusCode();

                var response = new TResponse();

                switch (response)
                {
                    case BaseResponseStream streamResponse:
                        streamResponse.Buffer = await httpResponse.Content.ReadAsByteArrayAsync();
                        response = (TResponse)(IResponse)streamResponse;
                        break;

                    default:
                        var rawJson = await httpResponse.Content.ReadAsStringAsync();
                        response = JsonConvert.DeserializeObject<TResponse>(rawJson);
                        response.RawJson = rawJson;
                        break;
                }

                response.RawQueryString = httpResponse.RequestMessage.RequestUri.PathAndQuery;
                response.Status = httpResponse.IsSuccessStatusCode
                    ? response.Status ?? Status.Ok
                    : Status.HttpError;

                return response;
            }
        }
        private async Task<TResponse> ProcessResponseAsync(HttpResponseMessage httpResponse)
        {
            if (httpResponse == null)
                throw new ArgumentNullException(nameof(httpResponse));

            using (httpResponse)
            {
                httpResponse.EnsureSuccessStatusCode();

                var response = new TResponse();

                switch (response)
                {
                    case BaseResponseStream streamResponse:
                        streamResponse.Buffer = await httpResponse.Content.ReadAsByteArrayAsync();
                        response = (TResponse)(IResponse)streamResponse;
                        break;

                    default:
                        var rawJson = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                        response = JsonConvert.DeserializeObject<TResponse>(rawJson);
                        response.RawJson = rawJson;
                        break;
                }

                response.RawQueryString = httpResponse.RequestMessage.RequestUri.PathAndQuery;
                response.Status = httpResponse.IsSuccessStatusCode
                    ? response.Status ?? Status.Ok
                    : Status.HttpError;

                return response;
            }
        }
    }
}