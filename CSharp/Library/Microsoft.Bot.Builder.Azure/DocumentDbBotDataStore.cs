﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Dialogs.Internals;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;

namespace Microsoft.Bot.Builder.Azure
{

    /// <summary>
    /// <see cref="IBotDataStore{T}"/> Implementation using Azure DocumentDb
    /// </summary>
    public class DocumentDbBotDataStore : IBotDataStore<BotData>
    {
        private static readonly TimeSpan MaxInitTime = TimeSpan.FromSeconds(5);

        private readonly IDocumentClient documentClient;
        private readonly string databaseId = "botdb";
        private readonly string collectionId = "botcollection";

        /// <summary>
        /// Creates an instance of the <see cref="IBotDataStore{T}"/> that uses the Azure DocumentDb.
        /// </summary>
        /// <param name="documentClient">The DocumentDb client to use.</param>
        public DocumentDbBotDataStore(IDocumentClient documentClient)
        {
            this.documentClient = documentClient;
            CreateDatabaseIfNotExistsAsync().Wait(MaxInitTime);
            CreateCollectionIfNotExistsAsync().Wait(MaxInitTime);
        }

        /// <summary>
        /// Creates an instance of the <see cref="IBotDataStore{T}"/> that uses the Azure DocumentDb.
        /// </summary>
        /// <param name="serviceEndpoint">The service endpoint to use to create the client.</param>
        /// <param name="authKey">The authorization key or resource token to use to create the client.</param>
        /// <param name="connectionPolicy">(Optional) The connection policy for the client.</param>
        /// <param name="consistencyLevel">(Optional) The default consistency policy for client operations.</param>
        /// <remarks>The service endpoint can be obtained from the Azure Management Portal. If you
        /// are connecting using one of the Master Keys, these can be obtained along with
        /// the endpoint from the Azure Management Portal If however you are connecting as
        /// a specific DocumentDB User, the value passed to authKeyOrResourceToken is the
        /// ResourceToken obtained from the permission feed for the user.
        /// Using Direct connectivity, wherever possible, is recommended.</remarks>
        public DocumentDbBotDataStore(Uri serviceEndpoint, string authKey, ConnectionPolicy connectionPolicy = null,
            ConsistencyLevel? consistencyLevel = null)
        {
            this.documentClient = new DocumentClient(serviceEndpoint, authKey, connectionPolicy, consistencyLevel);
            CreateDatabaseIfNotExistsAsync().Wait(MaxInitTime);
            CreateCollectionIfNotExistsAsync().Wait(MaxInitTime);
        }

        async Task<BotData> IBotDataStore<BotData>.LoadAsync(IAddress key, BotStoreType botStoreType,
            CancellationToken cancellationToken)
        {
            try
            {
                var entityKey = DocDbBotDataEntity.GetEntityKey(key, botStoreType);

                var response = await documentClient.ReadDocumentAsync(UriFactory.CreateDocumentUri(databaseId, collectionId, entityKey));

                DocDbBotDataEntity entity = (dynamic) response.Resource;
                return new BotData(response?.Resource.ETag, entity?.Data);
            }
            catch (DocumentClientException e)
            {
                if (e.StatusCode.HasValue && e.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    return new BotData(string.Empty, null);
                }

                throw new HttpException(e.StatusCode.HasValue ? (int) e.StatusCode.Value : 0, e.Message, e);
            }
        }

        async Task IBotDataStore<BotData>.SaveAsync(IAddress key, BotStoreType botStoreType, BotData botData,
            CancellationToken cancellationToken)
        {
            try
            {
                var requestOptions = new RequestOptions()
                {
                    AccessCondition = new AccessCondition()
                    {
                        Type = AccessConditionType.IfMatch,
                        Condition = botData.ETag
                    }
                };

                var entity = new DocDbBotDataEntity(key, botStoreType, botData);
                var entityKey = DocDbBotDataEntity.GetEntityKey(key, botStoreType);

                if (string.IsNullOrEmpty(botData.ETag))
                {
                    await documentClient.CreateDocumentAsync(UriFactory.CreateDocumentCollectionUri(databaseId, collectionId), entity, requestOptions);
                }
                else if (botData.ETag == "*")
                {
                    if (botData.Data != null)
                    {
                        await documentClient.UpsertDocumentAsync(UriFactory.CreateDocumentCollectionUri(databaseId, collectionId), entity, requestOptions);
                    }
                    else
                    {
                        await documentClient.DeleteDocumentAsync(UriFactory.CreateDocumentUri(databaseId, collectionId, entityKey), requestOptions);
                    }
                }
                else
                {
                    if (botData.Data != null)
                    {
                        await documentClient.ReplaceDocumentAsync(UriFactory.CreateDocumentUri(databaseId, collectionId, entityKey), entity, requestOptions);
                    }
                    else
                    {
                        await documentClient.DeleteDocumentAsync(UriFactory.CreateDocumentUri(databaseId, collectionId, entityKey), requestOptions);
                    }
                }
            }
            catch (DocumentClientException e)
            {
                if (e.StatusCode.HasValue && e.StatusCode.Value == HttpStatusCode.Conflict)
                {
                    throw new HttpException((int)HttpStatusCode.PreconditionFailed, e.Message, e);
                }

                throw new HttpException(e.StatusCode.HasValue ? (int)e.StatusCode.Value : 0, e.Message, e);
            }
        }

        Task<bool> IBotDataStore<BotData>.FlushAsync(IAddress key, CancellationToken cancellationToken)
        {
            // Everything is saved. Flush is no-op
            return Task.FromResult(true);
        }

        private async Task CreateDatabaseIfNotExistsAsync()
        {
            try
            {
                await documentClient.ReadDatabaseAsync(UriFactory.CreateDatabaseUri(databaseId));
            }
            catch (DocumentClientException e)
            {
                if (e.StatusCode == HttpStatusCode.NotFound)
                {
                    await documentClient.CreateDatabaseAsync(new Database { Id = databaseId });
                }
                else
                {
                    throw;
                }
            }
        }

        private async Task CreateCollectionIfNotExistsAsync()
        {
            try
            {
                await documentClient.ReadDocumentCollectionAsync(UriFactory.CreateDocumentCollectionUri(databaseId, collectionId));
            }
            catch (DocumentClientException e)
            {
                if (e.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    await documentClient.CreateDocumentCollectionAsync(
                        UriFactory.CreateDatabaseUri(databaseId),
                        new DocumentCollection { Id = collectionId });
                }
                else
                {
                    throw;
                }
            }
        }

        public async Task DeleteDatabaseIfExists()
        {
            try
            {
                await documentClient.ReadDatabaseAsync(UriFactory.CreateDatabaseUri(databaseId));
                await documentClient.DeleteDatabaseAsync(UriFactory.CreateDatabaseUri(databaseId));
            }
            catch (DocumentClientException e)
            {
                if (e.StatusCode == HttpStatusCode.NotFound)
                {
                    //Database already non existent, nothing to do here.
                }
                else
                {
                    throw;
                }
            }
        }
    }

    internal class BotDataDocDbKey
    {
        public BotDataDocDbKey(string partition, string row)
        {
            PartitionKey = partition;
            RowKey = row;
        }

        public string PartitionKey { get; private set; }
        public string RowKey { get; private set; }

    }

    internal class DocDbBotDataEntity
    {
        public DocDbBotDataEntity() { }

        internal DocDbBotDataEntity(IAddress key, BotStoreType botStoreType, BotData botData)
        {
            this.Id = GetEntityKey(key, botStoreType);
            this.BotId = key.BotId;
            this.ChannelId = key.ChannelId;
            this.ConversationId = key.ConversationId;
            this.UserId = key.UserId;
            this.Data = botData.Data;
        }

        public static string GetEntityKey(IAddress key, BotStoreType botStoreType)
        {
            switch (botStoreType)
            {
                case BotStoreType.BotConversationData:
                    return $"{key.ChannelId}:conversation{key.ConversationId.SanitizeForAzureKeys()}";

                case BotStoreType.BotUserData:
                    return $"{key.ChannelId}:user{key.UserId.SanitizeForAzureKeys()}";

                case BotStoreType.BotPrivateConversationData:
                    return $"{key.ChannelId}:private{key.ConversationId.SanitizeForAzureKeys()}:{key.UserId.SanitizeForAzureKeys()}";

                default:
                    throw new ArgumentException("Unsupported bot store type!");
            }
        }

        [JsonProperty(PropertyName = "id")]
        public string Id { get; set; }

        [JsonProperty(PropertyName = "botId")]
        public string BotId { get; set; }

        [JsonProperty(PropertyName = "channelId")]
        public string ChannelId { get; set; }

        [JsonProperty(PropertyName = "conversationId")]
        public string ConversationId { get; set; }

        [JsonProperty(PropertyName = "userId")]
        public string UserId { get; set; }

        [JsonProperty(PropertyName = "data")]
        public object Data { get; set; }
    }
}



