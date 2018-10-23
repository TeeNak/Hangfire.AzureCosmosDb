﻿using System;
using System.Threading;
using System.Threading.Tasks;

using Hangfire.Server;
using Hangfire.Logging;
using Microsoft.Azure.Documents.Client;

using Hangfire.Azure.Documents;
using Hangfire.Azure.Documents.Helper;

namespace Hangfire.Azure
{
#pragma warning disable 618
    internal class ExpirationManager : IServerComponent
#pragma warning restore 618
    {
        private static readonly ILog logger = LogProvider.For<ExpirationManager>();
        private const string DISTRIBUTED_LOCK_KEY = "expirationmanager";
        private static readonly TimeSpan defaultLockTimeout = TimeSpan.FromMinutes(5);
        private static readonly DocumentTypes[] documents = { DocumentTypes.Lock, DocumentTypes.Job, DocumentTypes.List, DocumentTypes.Set, DocumentTypes.Hash, DocumentTypes.Counter };
        private readonly DocumentDbStorage storage;
        private readonly Uri spDeleteExpiredDocumentsUri;

        public ExpirationManager(DocumentDbStorage storage)
        {
            this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
            spDeleteExpiredDocumentsUri = UriFactory.CreateStoredProcedureUri(storage.Options.DatabaseName, storage.Options.CollectionName, "deleteExpiredDocuments");
        }

        public void Execute(CancellationToken cancellationToken)
        {
            foreach (DocumentTypes type in documents)
            {
                logger.Debug($"Removing outdated records from the '{type}' document.");
                int expireOn = DateTime.UtcNow.ToEpoch();

                using (new DocumentDbDistributedLock(DISTRIBUTED_LOCK_KEY, defaultLockTimeout, storage))
                {
                    int deleted = 0;
                    ProcedureResponse response;
                    do
                    {
                        Task<StoredProcedureResponse<ProcedureResponse>> procedureTask = storage.Client.ExecuteStoredProcedureAsync<ProcedureResponse>(spDeleteExpiredDocumentsUri, (int)type, expireOn);
                        procedureTask.Wait(cancellationToken);

                        response = procedureTask.Result;
                        deleted += response.Affected;

                        // if the continuation is true; run the procedure again
                    } while (response.Continuation);

                    logger.Trace($"Outdated {deleted} records removed from the '{type}' document.");
                }

                cancellationToken.WaitHandle.WaitOne(storage.Options.ExpirationCheckInterval);
            }
        }
    }
}