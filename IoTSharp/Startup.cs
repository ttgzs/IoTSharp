﻿using IoTSharp.EventBus;
using EasyCaching.Core.Configurations;
using HealthChecks.UI.Client;
using InfluxDB.Client;
using IoTSharp.Controllers.Models;
using IoTSharp.Data;
using IoTSharp.FlowRuleEngine;
using IoTSharp.Interpreter;
using IoTSharp.Storage;
using Jdenticon.AspNetCore;
using Jdenticon.Rendering;
using IoTSharp.Data.Taos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MQTTnet.AspNetCore;
using Newtonsoft.Json.Serialization;
using Quartz;
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using IoTSharp.Gateways;
using Microsoft.Extensions.ObjectPool;
using MaiKeBing.HostedService.ZeroMQ;
using IoTSharp.TaskActions;
using IoTSharp.Contracts;
using IoTSharp.Data.Shardings;
using IoTSharp.Data.Shardings.Routes;
using IoTSharp.EventBus.CAP;
using IoTSharp.EventBus.Shashlik;
using Microsoft.EntityFrameworkCore;
using ShardingCore;
using Storage.Net;
using ShardingCore.TableExists.Abstractions;
using ShardingCore.TableExists;
using IoTSharp.Data.TimeSeries;

namespace IoTSharp
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; private set; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var settings = Configuration.Get<AppSettings>();
            services.Configure((Action<AppSettings>)(setting =>
            {
                var option = setting.MqttBroker;
                Configuration.Bind(setting);
            }));
            var healthChecksUI = services.AddHealthChecksUI(setup =>
            {
                setup.SetHeaderText("IoTSharp HealthChecks");
                //Maximum history entries by endpoint
                setup.MaximumHistoryEntriesPerEndpoint(50);
                setup.AddIoTSharpHealthCheckEndpoint();
            });

            var healthChecks = services.AddHealthChecks()
                .AddDiskStorageHealthCheck(dso =>
                {
                    System.IO.DriveInfo.GetDrives()
                        .Where(d => d.DriveType == System.IO.DriveType.Fixed)
                        .Select(f => f.Name).Distinct().ToList()
                        .ForEach(f => dso.AddDrive(f));
                }, name: "Disk Storage");

            switch (settings.DataBase)
            {
                case DataBaseType.MySql:
                    services.ConfigureMySql(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    break;

                case DataBaseType.SqlServer:
                    services.ConfigureSqlServer(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    break;

                case DataBaseType.Oracle:
                    services.ConfigureOracle(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    break;

                case DataBaseType.Sqlite:
                    services.ConfigureSqlite(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    break;
                case DataBaseType.InMemory:
                    services.ConfigureInMemory(settings.DbContextPoolSize, healthChecksUI);
                    settings.TelemetryStorage = TelemetryStorage.SingleTable;
                    break;
                case DataBaseType.Cassandra:
                    services.ConfigureCassandra(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    settings.TelemetryStorage = TelemetryStorage.SingleTable;
                    break;
                case DataBaseType.ClickHouse:
                    services.ConfigureClickHouse(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    settings.TelemetryStorage = TelemetryStorage.SingleTable;
                    break;
                case DataBaseType.PostgreSql:
                default:
                    services.ConfigureNpgsql(Configuration.GetConnectionString("IoTSharp"), settings.DbContextPoolSize, healthChecks, healthChecksUI);
                    break;
            }
            services.AddDatabaseDeveloperPageExceptionFilter();
            services.AddIdentity<IdentityUser, IdentityRole>()
                .AddRoles<IdentityRole>()
                .AddRoleManager<RoleManager<IdentityRole>>()
                .AddDefaultTokenProviders()
                .AddEntityFrameworkStores<ApplicationDbContext>();





            services.AddAuthentication(option =>
            {
                option.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                option.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                option.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            }).AddJwtBearer(options =>
            {
                options.SaveToken = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = Configuration["JwtIssuer"],
                    ValidAudience = Configuration["JwtAudience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Configuration["JwtKey"])),
                    //     ClockSkew=TimeSpan.Zero //JWT的缓冲时间默认5分钟，token实际过期时间为 appsettings.json 当中JwtExpireHours配置的时间（小时）加上这个时间。
                }; ;
            });

            services.AddCors();
            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.AddRinLogger();
                loggingBuilder.AddSimpleConsole();
            }
            );
            services.AddRin();
            services.AddOpenApiDocument(configure =>
            {
                Assembly assembly = typeof(Startup).GetTypeInfo().Assembly;
                var description = (AssemblyDescriptionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyDescriptionAttribute));
                configure.Title = typeof(Startup).GetTypeInfo().Assembly.GetName().Name;
                configure.Version = typeof(Startup).GetTypeInfo().Assembly.GetName().Version.ToString();
                configure.Description = description?.Description;
                configure.AddJWTSecurity();
            });

            services.AddTransient<ApplicationDBInitializer>();
            services.AddIoTSharpMqttServer(settings.MqttBroker);
            services.AddMqttClient(settings.MqttClient);
            services.AddSilkierQuartz(options =>
            {
                options.VirtualPathRoot = "/quartz";
                options.UseLocalTime = true;
                options.DefaultDateFormat = "yyyy-MM-dd";
                options.DefaultTimeFormat = "HH:mm:ss";
                options.CronExpressionOptions = new CronExpressionDescriptor.Options()
                {
                    DayOfWeekStartIndexZero = false //Quartz uses 1-7 as the range
                };
            }, authenticationOptions =>
            {
                authenticationOptions.AccessRequirement = SilkierQuartz.SilkierQuartzAuthenticationOptions.SimpleAccessRequirement.AllowAnonymous;//登录认证有问题
            }, stdSchedulerFactoryOption =>
            {
                stdSchedulerFactoryOption.Add("quartz.plugin.recentHistory.type", "Quartz.Plugins.RecentHistory.ExecutionHistoryPlugin, Quartz.Plugins.RecentHistory");
                stdSchedulerFactoryOption.Add("quartz.plugin.recentHistory.storeType", "Quartz.Plugins.RecentHistory.Impl.InProcExecutionHistoryStore, Quartz.Plugins.RecentHistory");
            }
        );
            services.AddResponseCompression();
            services.AddControllers();

            services.AddMemoryCache();
            string _hc_Caching = $"{nameof(CachingUseIn)}-{Enum.GetName(settings.CachingUseIn)}";
            services.AddEasyCaching(options =>
            {
                switch (settings.CachingUseIn)
                {
                    case CachingUseIn.Redis:
                        options.UseRedis(config =>
                        {
                            settings.CachingUseRedisHosts?.Split(';').ToList().ForEach(h =>
                            {
                                var hx = h.Split(':');
                                config.DBConfig.Endpoints.Add(new ServerEndPoint(hx[0], int.Parse(hx[1])));
                            });
                        }, "iotsharp");
                        healthChecks.AddRedis(settings.CachingUseRedisHosts, name: _hc_Caching);
                        break;

                    case CachingUseIn.LiteDB:
                        options.UseLiteDB(cfg => cfg.DBConfig = new EasyCaching.LiteDB.LiteDBDBOptions() { }, name: _hc_Caching);
                        break;

                    case CachingUseIn.InMemory:
                    default:
                        options.UseInMemory(_hc_Caching);
                        break;
                }
            });
            services.AddTelemetryStorage( settings, healthChecks,o=>
            {
                switch (settings.DataBase)
                {
                    case DataBaseType.MySql:
                        o.UseMySqlToSharding();
                        break;

                    case DataBaseType.SqlServer:
                        o.UseSqlServerToSharding();
                        break;

                    case DataBaseType.Oracle:
                        o.UseOracleToSharding();
                        break;

                    case DataBaseType.Sqlite:
                        o.UseSQLiteToSharding();
                        break;
                    case DataBaseType.PostgreSql:
                    default:
                        o.UseNpgsqlToSharding();
                        break;

                }
            });
            var zmq = Configuration.GetSection(nameof(ZMQOption)).Get<ZMQOption>();
            if (zmq != null)
            {
                services.AddHostedZeroMQ(cfg => cfg = zmq);
            }
            services.AddEventBus(opt =>
            {
                opt.AppSettings = settings;
                opt.EventBusStore = Configuration.GetConnectionString("EventBusStore");
                opt.EventBusMQ = Configuration.GetConnectionString("EventBusMQ");
                opt.HealthChecks = healthChecks;
                switch (settings.EventBus)
                {
                    case EventBusFramework.Shashlik:
                        opt.UserShashlik();
                        break;
                    case EventBusFramework.CAP:
                    default:
                        opt.UserCAP();
                        break;
                }
            });

            services.AddTransient(opts =>
            {
                return StorageFactory.Blobs.FromConnectionString(Configuration.GetConnectionString("BlobStorage") ?? $"disk://path={Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.Create)}/IoTSharp/");
            });


            services.Configure<BaiduTranslateProfile>(Configuration.GetSection("BaiduTranslateProfile"));
            services.AddControllers().AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
            });
            services.AddRazorPages();


            services.AddScriptEngines(Configuration.GetSection("EngineSetting"));
            services.AddTransient<FlowRuleProcessor>();
            services.AddTransient<CustomeAlarmPullExcutor>();
            services.AddSingleton<TaskExecutorHelper>();
            services.AddTransient<PublishAttributeDataTask>();
            services.AddTransient<PublishTelemetryDataTask>();
            services.AddTransient<PublishAlarmDataTask>();
            services.AddTransient<RawDataGateway>();
            services.AddTransient<KepServerEx>();
            
        }

      



        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ISchedulerFactory factory)
        {
            if (env.IsDevelopment() || !env.IsEnvironment("Production"))
            {
                    // Add: Enable request/response recording and serve a inspector frontend.
                    // Important: `UseRin` (Middlewares) must be top of the HTTP pipeline.
                    app.UseRin();

                    // Add(option): Enable ASP.NET Core MVC support if the project built with ASP.NET Core MVC
                    app.UseRinMvcSupport();

                    app.UseDeveloperExceptionPage();
                app.UseMigrationsEndPoint();
                // Add: Enable Exception recorder. this handler must be after `UseDeveloperExceptionPage`.
                app.UseRinDiagnosticsHandler();
            }
            else
            {
                app.UseExceptionHandler("/Home/Error");
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }
            //添加定时任务创建表
            var settings = Configuration.Get<AppSettings>();
            if (settings.TelemetryStorage == TelemetryStorage.Sharding)
            {
                app.ApplicationServices.UseAutoTryCompensateTable();
            }
            app.UseRouting();
            app.UseCors(option => option
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader());
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.UseResponseCompression();
            app.UseIotSharpMqttServer();
            app.UseSwaggerUi3();
            app.UseOpenApi();
            app.UseSilkierQuartz();
            app.UseEventBus(opt =>
            {
                var frp = app.ApplicationServices.GetService<FlowRuleProcessor>();
                return frp.RunRules;
            });

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMqtt("/mqtt");
                endpoints.MapHealthChecks("/healthz", new HealthCheckOptions()
                {
                    Predicate = _ => true,
                    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
                });
                endpoints.MapHealthChecksUI();
                endpoints.MapDefaultControllerRoute();
                endpoints.MapRazorPages();
            });
          
            app.UseJdenticon(defaultStyle =>
            {
                // Custom identicon style
                // https://jdenticon.com/icon-designer.html?config=8644440010c4330a24461852
                defaultStyle.Hues = new HueCollection { { 196, HueUnit.Degrees } };
                defaultStyle.BackColor = Color.FromRgba(134, 68, 68, 0);
                defaultStyle.ColorLightness = Jdenticon.Range.Create(0.36f, 0.70f);
                defaultStyle.GrayscaleLightness = Jdenticon.Range.Create(0.24f, 0.82f);
                defaultStyle.ColorSaturation = 0.51f;
                defaultStyle.GrayscaleSaturation = 0.10f;
            });
        }
    }
}