using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using MeterReaderWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeterReaderClient
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _config;
        private readonly ReadingFactory _readingFactory;
        private readonly ILoggerFactory _logerFactory;
        private MeterReadingService.MeterReadingServiceClient _client = null;
        private string _token;
        private DateTime _expiration = DateTime.MinValue;
        public Worker(ILogger<Worker> logger, IConfiguration config, ReadingFactory readingFactory, ILoggerFactory logerFactory)
        {
            _logger = logger;
            _config = config;
            _readingFactory = readingFactory;
            _logerFactory = logerFactory;
        }

        protected MeterReadingService.MeterReadingServiceClient Client
        {
            get
            {
                if(_client == null)
                {
                    ////for certificate
                     var cert = new X509Certificate2(_config["Service:CertFile"],_config["Service:CertPassword"]);
                     var handler = new HttpClientHandler();
                     handler.ClientCertificates.Add(cert);
                     var client = new HttpClient(handler);
                     //// end certificate
                    var opt = new GrpcChannelOptions()
                    {
                        HttpClient = client, // for certificate
                        LoggerFactory = _logerFactory
                    };
                    var channel = GrpcChannel.ForAddress(_config.GetValue<string>("Service:ServerUrl"), opt);
                    _client = new MeterReadingService.MeterReadingServiceClient(channel);
                }
                return _client;
            }
        }

        public bool NeedsLogin() => string.IsNullOrWhiteSpace(_token) || _expiration > DateTime.UtcNow;
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var customerId = _config.GetValue<int>("Service:CustomerId");
            var counter = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                counter++;
                //if (counter % 10 == 0)
                //{
                //    Console.WriteLine("Sending Diagnostics...");
                //    var stream = Client.SendDiagnostics();
                //    for (int i = 0; i < 5; i++)
                //    {
                //        var reading = await _readingFactory.Generate(customerId);
                //        await stream.RequestStream.WriteAsync(reading);
                //    }

                //    await stream.RequestStream.CompleteAsync();
                //}
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

                var pkt = new ReadingPacket {Successful = ReadingStatus.Success, Notes = "This is our Test!"};

                for (int i = 0; i < 5; i++)
                {
                    pkt.Readings.Add(await _readingFactory.Generate(customerId));
                }

                try
                {
                  //  if (!NeedsLogin() || await GenerateToken()) //for JWT use
                    {
                        // var headers = new Metadata(); //for JWT use
                        // headers.Add("Authorization", $"Bearer {_token}");//for JWT use

                        // var result = await Client.AddReadingAsync(pkt, headers: headers); // for JWT Use
                        var result = await Client.AddReadingAsync(pkt);
                        if (result.Success == ReadingStatus.Success)
                        {
                            _logger.LogInformation("Successfully send");
                        }
                        else
                        {
                            _logger.LogInformation("Send to failed");
                        }
                    }
                }
                catch (RpcException ex)
                {
                    if (ex.StatusCode == StatusCode.OutOfRange)
                    {
                        _logger.LogError($"Trailers: {ex.Trailers}");
                    } 
                    _logger.LogError($"Exception thrown: {ex}");
                }
               
                await Task.Delay(_config.GetValue<int>("Service:DelayInterval"), stoppingToken);
            }
        }

        private async Task<bool> GenerateToken()
        {
            var request = new TokenRequest
            {
                Username = _config["Service:Username"], Password = _config["Service:Password"]
            };
            var response = await Client.CreateTokenAsync(request);
            if (response.Success)
            {
                _token = response.Token;
                _expiration = response.Expiration.ToDateTime();
                return true;
            }

            return false;
        }
    }
}
