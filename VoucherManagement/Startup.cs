using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    using System.IO.Abstractions;
    using System.Net.Http;
    using System.Reflection;
    using System.Xml;
    using BusinessLogic;
    using BusinessLogic.EventHandling;
    using BusinessLogic.Manager;
    using BusinessLogic.RequestHandlers;
    using BusinessLogic.Requests;
    using BusinessLogic.Services;
    using Common;
    using EstateManagement.Client;
    using EstateReporting.Database;
    using EventStore.Client;
    using HealthChecks.UI.Client;
    using MediatR;
    using MessagingService.Client;
    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.AspNetCore.Diagnostics.HealthChecks;
    using Microsoft.Extensions.Diagnostics.HealthChecks;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Logging;
    using Microsoft.OpenApi.Models;
    using Models;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using NLog.Extensions.Logging;
    using SecurityService.Client;
    using Shared.DomainDrivenDesign.EventSourcing;
    using Shared.EntityFramework;
    using Shared.EntityFramework.ConnectionStringConfiguration;
    using Shared.EventStore.Aggregate;
    using Shared.EventStore.EventHandling;
    using Shared.EventStore.EventStore;
    using Shared.Extensions;
    using Shared.General;
    using Shared.Logger;
    using Shared.Repositories;
    using Swashbuckle.AspNetCore.Filters;
    using Swashbuckle.AspNetCore.SwaggerGen;
    using ILogger = Microsoft.Extensions.Logging.ILogger;

    [ExcludeFromCodeCoverage]
    public class Startup
    {
        public Startup(IWebHostEnvironment webHostEnvironment)
        {
            IConfigurationBuilder builder = new ConfigurationBuilder().SetBasePath(webHostEnvironment.ContentRootPath)
                                                                      .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                                                                      .AddJsonFile($"appsettings.{webHostEnvironment.EnvironmentName}.json", optional: true).AddEnvironmentVariables();

            Startup.Configuration = builder.Build();
            Startup.WebHostEnvironment = webHostEnvironment;
        }

        /// <summary>
        /// Gets or sets the configuration.
        /// </summary>
        /// <value>
        /// The configuration.
        /// </value>
        public static IConfigurationRoot Configuration { get; set; }

        /// <summary>
        /// Gets or sets the web host environment.
        /// </summary>
        /// <value>
        /// The web host environment.
        /// </value>
        public static IWebHostEnvironment WebHostEnvironment { get; set; }

        // This method gets called by the runtime. Use this method to add services to the container.
        /// <summary>
        /// Configures the services.
        /// </summary>
        /// <param name="services">The services.</param>
        public void ConfigureServices(IServiceCollection services)
        {
            ConfigurationReader.Initialise(Startup.Configuration);

            Startup.ConfigureEventStoreSettings();

            this.ConfigureMiddlewareServices(services);

            services.AddTransient<IMediator, Mediator>();

            ConfigurationReader.Initialise(Startup.Configuration);

            String connString = Startup.Configuration.GetValue<String>("EventStoreSettings:ConnectionString");
            String connectionName = Startup.Configuration.GetValue<String>("EventStoreSettings:ConnectionName");
            Int32 httpPort = Startup.Configuration.GetValue<Int32>("EventStoreSettings:HttpPort");

            Boolean useConnectionStringConfig = Boolean.Parse(ConfigurationReader.GetValue("AppSettings", "UseConnectionStringConfig"));

            if (useConnectionStringConfig)
            {
                String connectionStringConfigurationConnString = ConfigurationReader.GetConnectionString("ConnectionStringConfiguration");
                services.AddSingleton<IConnectionStringConfigurationRepository, ConnectionStringConfigurationRepository>();
                services.AddTransient<ConnectionStringConfigurationContext>(c =>
                                                                            {
                                                                                return new ConnectionStringConfigurationContext(connectionStringConfigurationConnString);
                                                                            });

                // TODO: Read this from a the database and set
            }
            else
            {
                services.AddSingleton<IConnectionStringConfigurationRepository, ConfigurationReaderConnectionStringRepository>();
                services.AddEventStoreClient(Startup.ConfigureEventStoreSettings);
                services.AddEventStoreProjectionManagementClient(Startup.ConfigureEventStoreSettings);
                services.AddEventStorePersistentSubscriptionsClient(Startup.ConfigureEventStoreSettings);
                services.AddSingleton<IConnectionStringConfigurationRepository, ConfigurationReaderConnectionStringRepository>();
            }

            services.AddSingleton<Func<String, EstateReportingContext>>(cont => (connectionString) => { return new EstateReportingContext(connectionString); });

            services.AddTransient<IEventStoreContext, EventStoreContext>();
            services.AddSingleton<IAggregateRepository<VoucherAggregate.VoucherAggregate, DomainEventRecord.DomainEvent>, AggregateRepository<VoucherAggregate.VoucherAggregate, DomainEventRecord.DomainEvent>>();
            services.AddSingleton<IVoucherDomainService, VoucherDomainService>();
            services.AddSingleton<IVoucherManagementManager, VoucherManagementManager>();
            services.AddSingleton<Factories.IModelFactory, Factories.ModelFactory>();
            services.AddSingleton<Func<String, EstateReportingContext>>(cont => (connectionString) => { return new EstateReportingContext(connectionString); });

            services.AddSingleton<Func<String, String>>(container => (serviceName) =>
                                                                     {
                                                                         return ConfigurationReader.GetBaseServerUri(serviceName).OriginalString;
                                                                     });

            HttpClientHandler httpClientHandler = new HttpClientHandler
                                                  {
                                                      ServerCertificateCustomValidationCallback = (message,
                                                                                                   certificate2,
                                                                                                   arg3,
                                                                                                   arg4) =>
                                                                                                  {
                                                                                                      return true;
                                                                                                  }
                                                  };
            HttpClient httpClient = new HttpClient(httpClientHandler);
            services.AddSingleton<HttpClient>(httpClient);
            services.AddSingleton<IEstateClient, EstateClient>();
            services.AddSingleton<ISecurityServiceClient, SecurityServiceClient>();
            services.AddSingleton<IFileSystem, FileSystem>();
            services.AddSingleton<IDbContextFactory<EstateReportingContext>, DbContextFactory<EstateReportingContext>>();
            // request & notification handlers
            services.AddTransient<ServiceFactory>(context =>
                                                  {
                                                      return t => context.GetService(t);
                                                  });

            services.AddSingleton<IRequestHandler<IssueVoucherRequest, IssueVoucherResponse>, VoucherManagementRequestHandler>();
            services.AddSingleton<IRequestHandler<RedeemVoucherRequest, RedeemVoucherResponse>, VoucherManagementRequestHandler>();

            Dictionary<String, String[]> eventHandlersConfiguration = new Dictionary<String, String[]>();

            if (Startup.Configuration != null)
            {
                IConfigurationSection section = Startup.Configuration.GetSection("AppSettings:EventHandlerConfiguration");

                if (section != null)
                {
                    Startup.Configuration.GetSection("AppSettings:EventHandlerConfiguration").Bind(eventHandlersConfiguration);
                }
            }
            services.AddSingleton<Dictionary<String, String[]>>(eventHandlersConfiguration);

            services.AddSingleton<Func<Type, IDomainEventHandler>>(container => (type) =>
                                                                                {
                                                                                    IDomainEventHandler handler = container.GetService(type) as IDomainEventHandler;
                                                                                    return handler;
                                                                                });

            services.AddSingleton<VoucherDomainEventHandler>();
            services.AddSingleton<IDomainEventHandlerResolver, DomainEventHandlerResolver>();
            services.AddSingleton<IMessagingServiceClient, MessagingServiceClient>();
        }

        private void ConfigureMiddlewareServices(IServiceCollection services)
        {
            services.AddHealthChecks()
                    //.AddEventStore(Startup.EventStoreClientSettings,
                    //               userCredentials: Startup.EventStoreClientSettings.DefaultCredentials,
                    //               name: "Eventstore",
                    //               failureStatus: HealthStatus.Unhealthy,
                    //               tags: new string[] { "db", "eventstore" })
                    .AddSqlServer(connectionString: ConfigurationReader.GetConnectionString("HealthCheck"),
                                  healthQuery: "SELECT 1;",
                                  name: "Read Model Server",
                                  failureStatus: HealthStatus.Degraded,
                                  tags: new string[] { "db", "sql", "sqlserver" })
                    .AddUrlGroup(new Uri($"{ConfigurationReader.GetValue("AppSettings", "MessagingServiceApi")}/health"),
                                 name: "Messaging Service",
                                 httpMethod: HttpMethod.Get,
                                 failureStatus: HealthStatus.Unhealthy,
                                 tags: new string[] { "messaging", "email" })
                    .AddUrlGroup(new Uri($"{ConfigurationReader.GetValue("AppSettings", "EstateManagementApi")}/health"),
                                 name: "Estate Management Service",
                                 httpMethod: HttpMethod.Get,
                                 failureStatus: HealthStatus.Unhealthy,
                                 tags: new string[] { "application", "estatemanagement" })
                    .AddUrlGroup(new Uri($"{ConfigurationReader.GetValue("SecurityConfiguration", "Authority")}/health"),
                                 name:"Security Service",
                                 httpMethod:HttpMethod.Get,
                                 failureStatus:HealthStatus.Unhealthy,
                                 tags:new string[] {"security", "authorisation"});

            
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                                   {
                                       Title = "Voucher Management API",
                                       Version = "1.0",
                                       Description = "A REST Api to manage the issuing and redemption of voucher transactions.",
                                       Contact = new OpenApiContact
                                                 {
                                                     Name = "Stuart Ferguson",
                                                     Email = "golfhandicapping@btinternet.com"
                                                 }
                                   });
                // add a custom operation filter which sets default values
                c.OperationFilter<SwaggerDefaultValues>();
                c.ExampleFilters();

                //Locate the XML files being generated by ASP.NET...
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                var xmlFiles = directory.GetFiles("*.xml");

                //... and tell Swagger to use those XML comments.
                foreach (FileInfo fileInfo in xmlFiles)
                {
                    c.IncludeXmlComments(fileInfo.FullName);
                }
            });

            services.AddSwaggerExamplesFromAssemblyOf<SwaggerJsonConverter>();

            IdentityModelEventSource.ShowPII = true;

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
                   .AddJwtBearer(options =>
                   {
                       options.BackchannelHttpHandler = new HttpClientHandler
                                                        {
                                                            ServerCertificateCustomValidationCallback =
                                                                (message, certificate, chain, sslPolicyErrors) => true
                                                        };
                       options.Authority = ConfigurationReader.GetValue("SecurityConfiguration", "Authority");
                       options.Audience = ConfigurationReader.GetValue("SecurityConfiguration", "ApiName");

                       options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters()
                                                           {
                                                               ValidateAudience = false,
                                                               ValidAudience = ConfigurationReader.GetValue("SecurityConfiguration", "ApiName"),
                                                               ValidIssuer = ConfigurationReader.GetValue("SecurityConfiguration", "Authority"),
                                                           };
                       options.IncludeErrorDetails = true;
                   });

            services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                options.SerializerSettings.TypeNameHandling = TypeNameHandling.None;
                options.SerializerSettings.Formatting = Newtonsoft.Json.Formatting.Indented;
                options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            });

            Assembly assembly = this.GetType().GetTypeInfo().Assembly;
            services.AddMvcCore().AddApplicationPart(assembly).AddControllersAsServices();
        }
        
        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        /// <summary>
        /// Configures the specified application.
        /// </summary>
        /// <param name="app">The application.</param>
        /// <param name="env">The env.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        /// <param name="provider">The provider.</param>
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILoggerFactory loggerFactory)
        {
            String nlogConfigFilename = "nlog.config";

            if (env.IsDevelopment())
            {
                nlogConfigFilename = $"nlog.{env.EnvironmentName}.config";
                app.UseDeveloperExceptionPage();
            }

            loggerFactory.ConfigureNLog(Path.Combine(env.ContentRootPath, nlogConfigFilename));
            loggerFactory.AddNLog();

            ILogger logger = loggerFactory.CreateLogger("VoucherManagement");

            Logger.Initialise(logger);

            app.AddRequestLogging();
            app.AddResponseLogging();
            app.AddExceptionHandler();

            app.UseRouting();

            app.UseAuthentication();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHealthChecks("health", new HealthCheckOptions()
                {
                    Predicate = _ => true,
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                });
            });

            app.UseSwagger();

            app.UseSwaggerUI();
        }

        /// <summary>
        /// The event store client settings
        /// </summary>
        private static EventStoreClientSettings EventStoreClientSettings;

        /// <summary>
        /// Configures the event store settings.
        /// </summary>
        /// <param name="settings">The settings.</param>
        private static void ConfigureEventStoreSettings(EventStoreClientSettings settings = null)
        {
            if (settings == null)
            {
                settings = new EventStoreClientSettings();
            }

            settings.CreateHttpMessageHandler = () => new SocketsHttpHandler
            {
                SslOptions =
                                                          {
                                                              RemoteCertificateValidationCallback = (sender,
                                                                                                     certificate,
                                                                                                     chain,
                                                                                                     errors) => true,
                                                          }
            };
            settings.ConnectionName = Startup.Configuration.GetValue<String>("EventStoreSettings:ConnectionName");
            settings.ConnectivitySettings = new EventStoreClientConnectivitySettings
            {
                Address = new Uri(Startup.Configuration.GetValue<String>("EventStoreSettings:ConnectionString")),
            };

            settings.DefaultCredentials = new UserCredentials(Startup.Configuration.GetValue<String>("EventStoreSettings:UserName"),
                                                              Startup.Configuration.GetValue<String>("EventStoreSettings:Password"));
            Startup.EventStoreClientSettings = settings;
        }
    }
}
