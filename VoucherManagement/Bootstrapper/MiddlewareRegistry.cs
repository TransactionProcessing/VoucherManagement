﻿namespace VoucherManagement.Bootstrapper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Abstractions;
    using System.Net.Http;
    using System.Reflection;
    using BusinessLogic;
    using BusinessLogic.EventHandling;
    using BusinessLogic.Manager;
    using BusinessLogic.RequestHandlers;
    using BusinessLogic.Requests;
    using BusinessLogic.Services;
    using Common;
    using EstateManagement.Client;
    using EstateReporting.Database;
    using Lamar;
    using MediatR;
    using MessagingService.Client;
    using Microsoft.AspNetCore.Authentication.JwtBearer;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Diagnostics.HealthChecks;
    using Microsoft.OpenApi.Models;
    using Models;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Serialization;
    using SecurityService.Client;
    using Shared.DomainDrivenDesign.EventSourcing;
    using Shared.EntityFramework;
    using Shared.EntityFramework.ConnectionStringConfiguration;
    using Shared.EventStore.Aggregate;
    using Shared.EventStore.EventHandling;
    using Shared.EventStore.EventStore;
    using Shared.Extensions;
    using Shared.General;
    using Shared.Repositories;
    using Swashbuckle.AspNetCore.Filters;

    public class MiddlewareRegistry : ServiceRegistry
    {
        public MiddlewareRegistry()
        {
            this.AddHealthChecks()
                    .AddSqlServer(connectionString: ConfigurationReader.GetConnectionString("HealthCheck"),
                                  healthQuery: "SELECT 1;",
                                  name: "Read Model Server",
                                  failureStatus: HealthStatus.Degraded,
                                  tags: new string[] { "db", "sql", "sqlserver" })
                    .AddMessagingService().AddEstateManagementService().AddSecurityService(ApiEndpointHttpHandler);


            this.AddSwaggerGen(c =>
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

            this.AddSwaggerExamplesFromAssemblyOf<SwaggerJsonConverter>();
            
            this.AddAuthentication(options =>
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

            this.AddControllers().AddNewtonsoftJson(options =>
                                                    {
                                                        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                                                        options.SerializerSettings.TypeNameHandling = TypeNameHandling.None;
                                                        options.SerializerSettings.Formatting = Newtonsoft.Json.Formatting.Indented;
                                                        options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                                                        options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
                                                    });

            Assembly assembly = this.GetType().GetTypeInfo().Assembly;
            this.AddMvcCore().AddApplicationPart(assembly).AddControllersAsServices();
        }

        private HttpClientHandler ApiEndpointHttpHandler(IServiceProvider serviceProvider)
        {
            return new HttpClientHandler
                   {
                       ServerCertificateCustomValidationCallback = (message,
                                                                    cert,
                                                                    chain,
                                                                    errors) =>
                                                                   {
                                                                       return true;
                                                                   }
                   };
        }
    }

    public class MediatorRegistry : ServiceRegistry
    {
        public MediatorRegistry()
        {
            this.AddTransient<IMediator, Mediator>();

            // request & notification handlers
            this.AddTransient<ServiceFactory>(context =>
                                              {
                                                  return t => context.GetService(t);
                                              });

            this.AddSingleton<IRequestHandler<IssueVoucherRequest, IssueVoucherResponse>, VoucherManagementRequestHandler>();
            this.AddSingleton<IRequestHandler<RedeemVoucherRequest, RedeemVoucherResponse>, VoucherManagementRequestHandler>();
        }
    }

    public class RepositoryRegistry : ServiceRegistry
    {
        public RepositoryRegistry()
        {
            Boolean useConnectionStringConfig = Boolean.Parse(ConfigurationReader.GetValue("AppSettings", "UseConnectionStringConfig"));

            if (useConnectionStringConfig)
            {
                String connectionStringConfigurationConnString = ConfigurationReader.GetConnectionString("ConnectionStringConfiguration");
                this.AddSingleton<IConnectionStringConfigurationRepository, ConnectionStringConfigurationRepository>();
                this.AddTransient<ConnectionStringConfigurationContext>(c =>
                                                                        {
                                                                            return new ConnectionStringConfigurationContext(connectionStringConfigurationConnString);
                                                                        });

                // TODO: Read this from a the database and set
            }
            else
            {
                this.AddSingleton<IConnectionStringConfigurationRepository, ConfigurationReaderConnectionStringRepository>();
                this.AddEventStoreClient(Startup.ConfigureEventStoreSettings);
                this.AddEventStoreProjectionManagementClient(Startup.ConfigureEventStoreSettings);
                this.AddEventStorePersistentSubscriptionsClient(Startup.ConfigureEventStoreSettings);
                this.AddSingleton<IConnectionStringConfigurationRepository, ConfigurationReaderConnectionStringRepository>();
            }

            this.AddSingleton<Func<String, EstateReportingGenericContext>>(cont => (connectionString) =>
                                                                                   {
                                                                                       String databaseEngine =
                                                                                           ConfigurationReader.GetValue("AppSettings", "DatabaseEngine");

                                                                                       return databaseEngine switch
                                                                                       {
                                                                                           "MySql" => new EstateReportingMySqlContext(connectionString),
                                                                                           "SqlServer" => new EstateReportingSqlServerContext(connectionString),
                                                                                           _ => throw new
                                                                                               NotSupportedException($"Unsupported Database Engine {databaseEngine}")
                                                                                       };
                                                                                   });

            this.AddTransient<IEventStoreContext, EventStoreContext>();
            this.AddSingleton<IAggregateRepository<VoucherAggregate.VoucherAggregate, DomainEventRecord.DomainEvent>, AggregateRepository<VoucherAggregate.VoucherAggregate, DomainEventRecord.DomainEvent>>();

            this.AddSingleton<IDbContextFactory<EstateReportingGenericContext>, DbContextFactory<EstateReportingGenericContext>>();
        }
    }

    public class ClientRegistry : ServiceRegistry
    {
        public ClientRegistry()
        {
            this.AddSingleton<Func<String, String>>(container => (serviceName) =>
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
            this.AddSingleton<HttpClient>(httpClient);
            this.AddSingleton<IEstateClient, EstateClient>();
            this.AddSingleton<ISecurityServiceClient, SecurityServiceClient>();
            this.AddSingleton<IMessagingServiceClient, MessagingServiceClient>();
        }
    }

    public class DomainEventHandlerRegistry : ServiceRegistry
    {
        public DomainEventHandlerRegistry()
        {
            Dictionary<String, String[]> eventHandlersConfiguration = new Dictionary<String, String[]>();

            if (Startup.Configuration != null)
            {
                IConfigurationSection section = Startup.Configuration.GetSection("AppSettings:EventHandlerConfiguration");

                if (section != null)
                {
                    Startup.Configuration.GetSection("AppSettings:EventHandlerConfiguration").Bind(eventHandlersConfiguration);
                }
            }
            this.AddSingleton<Dictionary<String, String[]>>(eventHandlersConfiguration);

            this.AddSingleton<Func<Type, IDomainEventHandler>>(container => (type) =>
                                                                            {
                                                                                IDomainEventHandler handler = container.GetService(type) as IDomainEventHandler;
                                                                                return handler;
                                                                            });

            this.AddSingleton<VoucherDomainEventHandler>();
            this.AddSingleton<IDomainEventHandlerResolver, DomainEventHandlerResolver>();
        }
    }

    public class DomainServiceRegistry : ServiceRegistry
    {
        public DomainServiceRegistry()
        {
            this.AddSingleton<IVoucherDomainService, VoucherDomainService>();
        }
    }

    public class ManagerRegistry : ServiceRegistry
    {
        public ManagerRegistry()
        {
            this.AddSingleton<IVoucherManagementManager, VoucherManagementManager>();
        }
    }

    public class MiscRegistry : ServiceRegistry
    {
        public MiscRegistry()
        {
            this.AddSingleton<Factories.IModelFactory, Factories.ModelFactory>();
            this.AddSingleton<IFileSystem, FileSystem>();
        }
    }
}