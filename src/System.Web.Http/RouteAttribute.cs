﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved. See License.txt in the project root for license information.

using System.Diagnostics.Contracts;
using System.Web.Http.Routing;

namespace System.Web.Http
{
    /// <summary>
    /// Place on a controller or action to expose it directly via a route. 
    /// When placed on a controller, it applies to actions that do not have any <see cref="RouteAttribute"/>s on them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class RouteAttribute : Attribute, IHttpRouteInfoProvider, IDirectRouteProvider
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RouteAttribute" /> class.
        /// </summary>
        public RouteAttribute()
        {
            Template = String.Empty;
        }
        
        /// <summary>
        /// Initializes a new instance of the <see cref="RouteAttribute" /> class.
        /// </summary>
        /// <param name="template">The route template describing the URI pattern to match against.</param>
        public RouteAttribute(string template)
        {
            if (template == null)
            {
                throw Error.ArgumentNull("template");
            }
            Template = template;
        }

        /// <inheritdoc />
        public string Name { get; set; }

        /// <inheritdoc />
        public int Order { get; set; }

        /// <inheritdoc />
        public string Template { get; private set; }

        HttpRouteEntry IDirectRouteProvider.CreateRoute(DirectRouteProviderContext context)
        {
            Contract.Assert(context != null);

            DirectRouteBuilder builder = context.CreateBuilder(Template);
            Contract.Assert(builder != null);

            builder.Name = Name;
            builder.Order = Order;
            return builder.Build();
        }
    }
}
