// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Http.Internal
{
    public sealed class DefaultHttpResponse : HttpResponse
    {
        // Lambdas hoisted to static readonly fields to improve inlining https://github.com/dotnet/roslyn/issues/13624
        private readonly static Func<IFeatureCollection, IHttpResponseFeature> _nullResponseFeature = f => null;
        private readonly static Func<IFeatureCollection, IResponseCookiesFeature> _newResponseCookiesFeature = f => new ResponseCookiesFeature(f);

        private readonly DefaultHttpContext _context;
        private FeatureReferences<FeatureInterfaces> _features;

        public DefaultHttpResponse(DefaultHttpContext context, int featuresVersion)
        {
            _context = context;
            Initialize(context.Features, featuresVersion);
        }

        public void Initialize(IFeatureCollection features, int featuresVersion)
        {
            // Speculatively populate always used features for current version
            _features = new FeatureReferences<FeatureInterfaces>(features, featuresVersion);
            _features.Cache.Response = (IHttpResponseFeature)features[typeof(IHttpResponseFeature)];
        }

        private IHttpResponseFeature HttpResponseFeature =>
            _features.Fetch(ref _features.Cache.Response, _nullResponseFeature);

        private IResponseCookiesFeature ResponseCookiesFeature =>
            _features.Fetch(ref _features.Cache.Cookies, _newResponseCookiesFeature);


        public override HttpContext HttpContext => _context;

        public override int StatusCode
        {
            get { return HttpResponseFeature.StatusCode; }
            set { HttpResponseFeature.StatusCode = value; }
        }

        public override IHeaderDictionary Headers
        {
            get { return HttpResponseFeature.Headers; }
        }

        public override Stream Body
        {
            get { return HttpResponseFeature.Body; }
            set { HttpResponseFeature.Body = value; }
        }

        public override long? ContentLength
        {
            get { return Headers.ContentLength; }
            set { Headers.ContentLength = value; }
        }

        public override string ContentType
        {
            get
            {
                return Headers[HeaderNames.ContentType];
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    HttpResponseFeature.Headers.Remove(HeaderNames.ContentType);
                }
                else
                {
                    HttpResponseFeature.Headers[HeaderNames.ContentType] = value;
                }
            }
        }

        public override IResponseCookies Cookies
        {
            get { return ResponseCookiesFeature.Cookies; }
        }

        public override bool HasStarted
        {
            get { return HttpResponseFeature.HasStarted; }
        }

        public override void OnStarting(Func<object, Task> callback, object state)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            HttpResponseFeature.OnStarting(callback, state);
        }

        public override void OnCompleted(Func<object, Task> callback, object state)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            HttpResponseFeature.OnCompleted(callback, state);
        }

        public override void Redirect(string location, bool permanent)
        {
            if (permanent)
            {
                HttpResponseFeature.StatusCode = 301;
            }
            else
            {
                HttpResponseFeature.StatusCode = 302;
            }

            Headers[HeaderNames.Location] = location;
        }

        public void Uninitialize()
        {
            _features = default(FeatureReferences<FeatureInterfaces>);
        }

        struct FeatureInterfaces
        {
            public IHttpResponseFeature Response;
            public IResponseCookiesFeature Cookies;
        }
    }
}