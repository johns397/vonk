using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vonk.Core.Context;
using Vonk.Core.Metadata;
using Vonk.Core.Pluggability;
using Vonk.Core.Pluggability.ContextAware;

namespace Vonk.Plugin.EverythingOperation
{
    [VonkConfiguration(order: 4900, isLicensedAs: "http://fire.ly/vonk/plugins/everything")]
    public static class EverythingOperationConfiguration
    {
        // Add services here to the DI system of ASP.NET Core
        public static IServiceCollection ConfigureServices(IServiceCollection services)
        {
            services.TryAddScoped<EverythingService>(); // $everything implementation
            services.TryAddContextAware<ICapabilityStatementContributor, EverythingOperationConformanceContributor>
                (ServiceLifetime.Transient);
            return services;
        }

        // Add middleware to the pipeline being built with the builder
        public static IApplicationBuilder Configure(IApplicationBuilder builder)
        {
            // Register interactions
            builder
                .OnCustomInteraction(VonkInteraction.instance_custom, "everything")
                .AndResourceTypes(new[] { "Patient" })
                .AndMethod("GET")
                .HandleAsyncWith<EverythingService>((svc, context)
                    => svc.PatientInstanceGET(context));

            return builder;
        }
    }
}
