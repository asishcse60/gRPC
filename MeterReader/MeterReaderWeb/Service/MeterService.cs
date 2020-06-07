using System;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MeterReaderLib;
using MeterReaderLib.Models;
using MeterReaderWeb.Data;
using MeterReaderWeb.Data.Entities;
using MeterReaderWeb.Services;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace MeterReaderWeb.Service
{
    //[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)] // Use JWT or Certificate
    [Authorize(AuthenticationSchemes = CertificateAuthenticationDefaults.AuthenticationScheme)]
    public class MeterService : MeterReadingService.MeterReadingServiceBase
    {
        private readonly ILogger<MeterService> _logger;
        private readonly IReadingRepository _repositoryContext;
        private readonly JwtTokenValidationService _tokenService;

        public MeterService(ILogger<MeterService>logger, IReadingRepository repositoryContext, JwtTokenValidationService tokenService)
        {
            _logger = logger;
            _repositoryContext = repositoryContext;
            _tokenService = tokenService;
        }

        [AllowAnonymous]
        public override async Task<TokenResponse> CreateToken(TokenRequest request, ServerCallContext context)
        {
            var response =await _tokenService.GenerateTokenModelAsync(new CredentialModel()
            {
                UserName = request.Username,
                Passcode = request.Password
            });
            if (response.Success)
            {
                return new TokenResponse()
                {
                    Token = response.Token,
                    Expiration = Timestamp.FromDateTime(response.Expiration),
                    Success = true
                };
            }

            return new TokenResponse()
            {
                Success = false
            };
        }

        public override async Task<Empty> SendDiagnostics(IAsyncStreamReader<ReadingMessage> requestStream, ServerCallContext context)
        {
            var theTask = Task.Run(async () =>
            {
                await foreach (var readingStream in requestStream.ReadAllAsync())
                {
                    _logger.LogInformation($"Received reading: {readingStream}");
                }
            });
            await theTask;

            return new Empty();
        }

        public override async Task<StatusMessage> AddReading(ReadingPacket request, ServerCallContext context)
        {
            var result = new StatusMessage {Success = ReadingStatus.Failure};
            if (request.Successful == ReadingStatus.Success)
            {
                try
                {
                    foreach (var readingPack in request.Readings)
                    {
                        if (readingPack.ReadingValue < 1000)
                        {
                            _logger.LogDebug("Reading value below acceptable level");
                            var trailer = new Metadata()
                            {
                                {"BadValue", readingPack.ReadingValue.ToString()},
                                {"Field", "ReadingValue"},
                                {"Message", "Readings are invalid"}
                            };

                            throw new RpcException(new Status(StatusCode.OutOfRange, "Value too low"), trailer);
                        }

                        var model = new MeterReading
                        {
                            CustomerId = readingPack.CustomerId,
                            Value = readingPack.ReadingValue,
                            ReadingDate = readingPack.ReadingTime.ToDateTime()
                        };
                        _repositoryContext.AddEntity(model);
                    }

                    if (await _repositoryContext.SaveAllAsync())
                    {
                        _logger.LogInformation($"Stored {request.Readings.Count} New Readings...");
                        result.Success = ReadingStatus.Success;
                    }
                }
                catch (RpcException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.LogError($"Exception thrown during the save meter readings: {e}");
                    throw new RpcException(Status.DefaultCancelled, "Exception thrown during the meter reading process");
                }
            }
            return result;
        }
    }
}
