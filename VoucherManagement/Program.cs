using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VoucherManagement
{
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Net.Http;
    using EventStore.Client;
    using Lamar.Microsoft.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection;
    using Shared.EventStore.Aggregate;
    using Shared.EventStore.EventHandling;
    using Shared.EventStore.Subscriptions;
    using Shared.Logger;
    using Voucher.DomainEvents;

    [ExcludeFromCodeCoverage]
    public class Program
    {
        public static void Main(string[] args)
        {
            Program.CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args)
        {
            Console.Title = "Voucher Management";

            //At this stage, we only need our hosting file for ip and ports
            IConfigurationRoot config = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
                                                                  .AddJsonFile("hosting.json", optional: true)
                                                                  .AddJsonFile("hosting.development.json", optional: true)
                                                                  .AddEnvironmentVariables().Build();

            IHostBuilder hostBuilder = Host.CreateDefaultBuilder(args);
            hostBuilder.UseLamar();
            hostBuilder.ConfigureWebHostDefaults(webBuilder =>
                                                 {
                                                     webBuilder.UseStartup<Startup>();
                                                     webBuilder.UseConfiguration(config);
                                                     webBuilder.UseKestrel();
                                                 });

            return hostBuilder;
        }
    }
}
