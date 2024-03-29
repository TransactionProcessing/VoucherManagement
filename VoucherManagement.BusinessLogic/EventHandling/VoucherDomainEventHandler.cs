﻿namespace VoucherManagement.BusinessLogic.EventHandling
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.IO.Abstractions;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using System.Threading.Tasks;
    using EstateReporting.Database;
    using EstateReporting.Database.Entities;
    using MessagingService.Client;
    using MessagingService.DataTransferObjects;
    using Microsoft.EntityFrameworkCore;
    using SecurityService.Client;
    using SecurityService.DataTransferObjects.Responses;
    using Shared.DomainDrivenDesign.EventSourcing;
    using Shared.EventStore.Aggregate;
    using Shared.EventStore.EventHandling;
    using Shared.EventStore.EventStore;
    using Shared.General;
    using Shared.Logger;
    using Voucher.DomainEvents;
    using VoucherAggregate;
    using Voucher = Models.Voucher;
    
    /// <summary>
    /// 
    /// </summary>
    /// <seealso cref="IDomainEventHandler" />
    public class VoucherDomainEventHandler : IDomainEventHandler
    {
        #region Fields

        /// <summary>
        /// The database context factory
        /// </summary>
        private readonly Shared.EntityFramework.IDbContextFactory<EstateReportingGenericContext> DbContextFactory;

        /// <summary>
        /// The file system
        /// </summary>
        private readonly IFileSystem FileSystem;

        /// <summary>
        /// The messaging service client
        /// </summary>
        private readonly IMessagingServiceClient MessagingServiceClient;

        /// <summary>
        /// The security service client
        /// </summary>
        private readonly ISecurityServiceClient SecurityServiceClient;

        /// <summary>
        /// The token response
        /// </summary>
        private TokenResponse TokenResponse;

        /// <summary>
        /// The voucher aggregate repository
        /// </summary>
        private readonly IAggregateRepository<VoucherAggregate, DomainEvent> VoucherAggregateRepository;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="VoucherDomainEventHandler" /> class.
        /// </summary>
        /// <param name="securityServiceClient">The security service client.</param>
        /// <param name="voucherAggregateRepository">The voucher aggregate repository.</param>
        /// <param name="dbContextFactory">The database context factory.</param>
        /// <param name="messagingServiceClient">The messaging service client.</param>
        /// <param name="fileSystem">The file system.</param>
        public VoucherDomainEventHandler(ISecurityServiceClient securityServiceClient,
                                         IAggregateRepository<VoucherAggregate, DomainEvent> voucherAggregateRepository,
                                         Shared.EntityFramework.IDbContextFactory<EstateReportingGenericContext> dbContextFactory,
                                         IMessagingServiceClient messagingServiceClient,
                                         IFileSystem fileSystem)
        {
            this.SecurityServiceClient = securityServiceClient;
            this.VoucherAggregateRepository = voucherAggregateRepository;
            this.DbContextFactory = dbContextFactory;
            this.MessagingServiceClient = messagingServiceClient;
            this.FileSystem = fileSystem;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the email voucher message.
        /// </summary>
        /// <param name="voucherModel">The voucher model.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public async Task<String> GetEmailVoucherMessage(Voucher voucherModel,
                                                         CancellationToken cancellationToken)
        {
            IDirectoryInfo path = this.FileSystem.Directory.GetParent(Assembly.GetExecutingAssembly().Location);

            String fileData = await this.FileSystem.File.ReadAllTextAsync($"{path}/VoucherMessages/VoucherEmail.html", cancellationToken);

            PropertyInfo[] voucherProperties = voucherModel.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // Do the replaces for the transaction
            foreach (PropertyInfo propertyInfo in voucherProperties)
            {
                fileData = fileData.Replace($"[{propertyInfo.Name}]", propertyInfo.GetValue(voucherModel)?.ToString());
            }

            var voucherOperator = await this.GetVoucherOperator(voucherModel, cancellationToken);

            fileData = fileData.Replace("[OperatorIdentifier]", voucherOperator);

            return fileData;
        }

        /// <summary>
        /// Handles the specified domain event.
        /// </summary>
        /// <param name="domainEvent">The domain event.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task Handle(IDomainEvent domainEvent,
                                 CancellationToken cancellationToken)
        {
            await this.HandleSpecificDomainEvent((dynamic)domainEvent, cancellationToken);
        }

        /// <summary>
        /// Gets the token.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        [ExcludeFromCodeCoverage]
        private async Task<TokenResponse> GetToken(CancellationToken cancellationToken)
        {
            // Get a token to talk to the estate service
            String clientId = ConfigurationReader.GetValue("AppSettings", "ClientId");
            String clientSecret = ConfigurationReader.GetValue("AppSettings", "ClientSecret");
            Logger.LogInformation($"Client Id is {clientId}");
            Logger.LogInformation($"Client Secret is {clientSecret}");

            if (this.TokenResponse == null)
            {
                TokenResponse token = await this.SecurityServiceClient.GetToken(clientId, clientSecret, cancellationToken);
                Logger.LogInformation($"Token is {token.AccessToken}");
                return token;
            }

            if (this.TokenResponse.Expires.UtcDateTime.Subtract(DateTime.UtcNow) < TimeSpan.FromMinutes(2))
            {
                Logger.LogInformation($"Token is about to expire at {this.TokenResponse.Expires.DateTime:O}");
                TokenResponse token = await this.SecurityServiceClient.GetToken(clientId, clientSecret, cancellationToken);
                Logger.LogInformation($"Token is {token.AccessToken}");
                return token;
            }

            return this.TokenResponse;
        }

        /// <summary>
        /// Gets the voucher operator.
        /// </summary>
        /// <param name="voucherModel">The voucher model.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        private async Task<String> GetVoucherOperator(Voucher voucherModel,
                                                      CancellationToken cancellationToken)
        {
            EstateReportingGenericContext context = await this.DbContextFactory.GetContext(voucherModel.EstateId, cancellationToken);

            Transaction transaction = await context.Transactions.SingleOrDefaultAsync(t => t.TransactionId == voucherModel.TransactionId, cancellationToken);
            Contract contract = await context.Contracts.SingleOrDefaultAsync(c => c.ContractId == transaction.ContractId);

            return contract.Description;
        }

        /// <summary>
        /// Handles the specific domain event.
        /// </summary>
        /// <param name="domainEvent">The domain event.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        private async Task HandleSpecificDomainEvent(VoucherIssuedEvent domainEvent,
                                                     CancellationToken cancellationToken)
        {
            // Get the voucher aggregate
            VoucherAggregate voucherAggregate = await this.VoucherAggregateRepository.GetLatestVersion(domainEvent.AggregateId, cancellationToken);
            Voucher voucherModel = voucherAggregate.GetVoucher();
            this.TokenResponse = await this.GetToken(cancellationToken);
            if (string.IsNullOrEmpty(voucherModel.RecipientEmail) == false)
            {
                String message = await this.GetEmailVoucherMessage(voucherModel, cancellationToken);

                SendEmailRequest request = new SendEmailRequest
                                           {
                                               Body = message,
                                               ConnectionIdentifier = domainEvent.EstateId,
                                               FromAddress = "golfhandicapping@btinternet.com", // TODO: lookup from config
                                               IsHtml = true,
                                               MessageId = domainEvent.EventId,
                                               Subject = "Voucher Issue",
                                               ToAddresses = new List<String>
                                                             {
                                                                 voucherModel.RecipientEmail
                                                             }
                                           };

                await this.MessagingServiceClient.SendEmail(this.TokenResponse.AccessToken, request, cancellationToken);
            }

            if (String.IsNullOrEmpty(voucherModel.RecipientMobile) == false)
            {
                String message = await this.GetSMSVoucherMessage(voucherModel, cancellationToken);

                SendSMSRequest request = new SendSMSRequest
                                         {
                                             ConnectionIdentifier = domainEvent.EstateId,
                                             Destination = domainEvent.RecipientMobile,
                                             Message = message,
                                             MessageId = domainEvent.EventId,
                                             Sender = "Your Voucher"
                                         };

                await this.MessagingServiceClient.SendSMS(this.TokenResponse.AccessToken, request, cancellationToken);
            }
        }

        /// <summary>
        /// Gets the SMS voucher message.
        /// </summary>
        /// <param name="voucherModel">The voucher model.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        private async Task<String> GetSMSVoucherMessage(Voucher voucherModel,
                                                        CancellationToken cancellationToken)
        {
            IDirectoryInfo path = this.FileSystem.Directory.GetParent(Assembly.GetExecutingAssembly().Location);

            String fileData = await this.FileSystem.File.ReadAllTextAsync($"{path}/VoucherMessages/VoucherSMS.txt", cancellationToken);

            PropertyInfo[] voucherProperties = voucherModel.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

            // Do the replaces for the transaction
            foreach (PropertyInfo propertyInfo in voucherProperties)
            {
                fileData = fileData.Replace($"[{propertyInfo.Name}]", propertyInfo.GetValue(voucherModel)?.ToString());
            }

            String voucherOperator = await this.GetVoucherOperator(voucherModel, cancellationToken);

            fileData = fileData.Replace("[OperatorIdentifier]", voucherOperator);

            return fileData;
        }

        #endregion
    }
}