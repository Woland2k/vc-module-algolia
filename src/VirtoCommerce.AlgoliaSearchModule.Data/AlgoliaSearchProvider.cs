using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Algolia.Search.Clients;
using Algolia.Search.Models.Common;
using Algolia.Search.Models.Settings;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.SearchModule.Core.Exceptions;
using VirtoCommerce.SearchModule.Core.Model;
using VirtoCommerce.SearchModule.Core.Services;
using SearchRequest = VirtoCommerce.SearchModule.Core.Model.SearchRequest;

/// <summary>
/// Based on the document from https://www.algolia.com/doc/guides/getting-started/quick-start/tutorials/quick-start-with-the-api-client/csharp/?client=csharp
/// </summary>

namespace VirtoCommerce.AlgoliaSearchModule.Data
{
    public class AlgoliaSearchProvider : ISearchProvider
    {
        private readonly AlgoliaSearchOptions _algoliaSearchOptions;
        private readonly SearchOptions _searchOptions;
        private readonly ISettingsManager _settingsManager;

        public AlgoliaSearchProvider(IOptions<AlgoliaSearchOptions> algoliaSearchOptions, IOptions<SearchOptions> searchOptions, ISettingsManager settingsManager)
        {
            if (algoliaSearchOptions == null)
                throw new ArgumentNullException(nameof(algoliaSearchOptions));

            if (searchOptions == null)
                throw new ArgumentNullException(nameof(searchOptions));

            _algoliaSearchOptions = algoliaSearchOptions.Value;
            _searchOptions = searchOptions.Value;

            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        }

        private SearchClient _client;
        protected SearchClient Client => _client ??= CreateSearchServiceClient();

        public virtual async Task DeleteIndexAsync(string documentType)
        {
            if (string.IsNullOrEmpty(documentType))
                throw new ArgumentNullException(nameof(documentType));

            try
            {
                var indexName = GetIndexName(documentType);

                if (await IndexExistsAsync(indexName))
                {
                    var index = Client.InitIndex(indexName);
                    await index.DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                ThrowException("Failed to delete index", ex);
            }
        }

        public virtual async Task<IndexingResult> IndexAsync(string documentType, IList<IndexDocument> documents)
        {
            var indexName = GetIndexName(documentType);
            var providerDocuments = documents.Select(document => ConvertToProviderDocument(document, documentType)).ToList();

            //var indexExists = await IndexExistsAsync(documentType);
            var index = Client.InitIndex(indexName);

            // get current setting, so we can update them with new fields if needed
            var settings = await index.ExistsAsync() ? await index.GetSettingsAsync() : new IndexSettings();
            var settingHasChanges = false;

            // define searchable attributes
            foreach(var document in documents)
            {
                foreach (var field in document.Fields.OrderBy(f => f.Name))
                {
                    var fieldName = AlgoliaSearchHelper.ToAlgoliaFieldName(field.Name);
                    if (field.IsSearchable)
                    {
                        if (settings.SearchableAttributes == null)
                        {
                            settings.SearchableAttributes = new List<string>();
                        }

                        if (!settings.SearchableAttributes.Contains(fieldName))
                        {
                            settings.SearchableAttributes.Add(fieldName);
                            settingHasChanges = true;
                        }
                    }

                    if (field.IsFilterable)
                    {
                        if (settings.AttributesForFaceting == null)
                        {
                            settings.AttributesForFaceting = new List<string>();
                        }

                        if (!settings.AttributesForFaceting.Contains(fieldName))
                        {
                            settings.AttributesForFaceting.Add(fieldName);
                            settingHasChanges = true;
                        }
                    }

                    if (field.IsRetrievable)
                    {
                        if (settings.AttributesToRetrieve == null)
                        {
                            settings.AttributesToRetrieve = new List<string>();
                        }

                        if (!settings.AttributesToRetrieve.Contains(fieldName))
                        {
                            settings.AttributesToRetrieve.Add(fieldName);
                            settingHasChanges = true;
                        }
                    }
                }
            }

            // set replicas
            var existingReplicas = settings.Replicas;

            if (existingReplicas == null)
                existingReplicas = new List<string>();

            if (_algoliaSearchOptions.Replicas != null && _algoliaSearchOptions.Replicas.Length > 0)
            {
                var replicaNames = _algoliaSearchOptions.Replicas.Select(x => AlgoliaSearchHelper.ToAlgoliaReplicaName(indexName, x)).ToList();

                if (!Enumerable.SequenceEqual(existingReplicas, replicaNames))
                {
                    settingHasChanges = true;

                    settings.Replicas = existingReplicas.Union(replicaNames).ToList();

                    // set sorting field for each replica
                    foreach (var replica in _algoliaSearchOptions.Replicas)
                    {
                        var replicaName = AlgoliaSearchHelper.ToAlgoliaReplicaName(indexName, replica);
                        var replicaIndex = Client.InitIndex(replicaName);
                        var replicaSetting = new IndexSettings()
                        {
                            CustomRanking =
                            new List<string> { replica.IsDescending ? $"desc({replica.FieldName})" : $"asc({replica.FieldName})" }
                        };
                        await replicaIndex.SetSettingsAsync(replicaSetting);
                    }
                }
            }

            // only update index if there are changes
            if(settingHasChanges)
                await index.SetSettingsAsync(settings, forwardToReplicas: true);

            var response = await index.SaveObjectsAsync(providerDocuments);
            return CreateIndexingResult(response);
        }

        public virtual async Task<IndexingResult> RemoveAsync(string documentType, IList<IndexDocument> documents)
        {
            IndexingResult result;

            try
            {
                var indexName = GetIndexName(documentType);
                var index = Client.InitIndex(indexName);

                var ids = documents.Select(d => d.Id);
                var response = await index.DeleteObjectsAsync(ids.ToArray());
                result = CreateIndexingResult(response);
            }
            catch (Exception ex)
            {
                throw new SearchException(ex.Message, ex);
            }

            return result;
        }

        public virtual async Task<SearchResponse> SearchAsync(string documentType, SearchRequest request)
        {
            var indexName = AlgoliaSearchHelper.ToAlgoliaIndexName(GetIndexName(documentType), request.Sorting);

            try
            {
                var indexClient = Client.InitIndex(indexName);

                var providerQuery = new AlgoliaSearchRequestBuilder().BuildRequest(request, indexName);
                var response = await indexClient.SearchAsync<SearchDocument>(providerQuery);

                var result = response.ToSearchResponse(request);
                return result;
            }
            catch (Exception ex)
            {
                throw new SearchException(ex.Message, ex);
            }
        }

        protected virtual AlgoliaIndexDocument ConvertToProviderDocument(IndexDocument document, string documentType)
        {
            var result = new AlgoliaIndexDocument { ObjectID = document.Id };

            result.Add(AlgoliaSearchHelper.RawKeyFieldName, document.Id);

            foreach (var field in document.Fields.OrderBy(f => f.Name))
            {
                var fieldName = AlgoliaSearchHelper.ToAlgoliaFieldName(field.Name);

                if (result.ContainsKey(fieldName))
                {
                    var newValues = new List<object>();

                    var currentValue = result[fieldName];

                    if (currentValue is object[] currentValues)
                    {
                        newValues.AddRange(currentValues);
                    }
                    else
                    {
                        newValues.Add(currentValue);
                    }

                    newValues.AddRange(field.Values);
                    result[fieldName] = newValues.ToArray();
                }
                else
                {
                    var isCollection = field.IsCollection || field.Values.Count > 1;

                    // TODO: handle GEO POINT
                    var point = field.Value as GeoPoint;
                    var value = isCollection ? field.Values : field.Value;

                    result.Add(fieldName, value);
                }
            }

            return result;
        }


        protected virtual IndexingResult CreateIndexingResult(BatchIndexingResponse results)
        {
            var ids = new List<string>();

            foreach(var response in results.Responses)
            {
                ids.AddRange(response.ObjectIDs);
            }
            return new IndexingResult
            {
                Items = ids.Select(r => new IndexingResultItem
                {
                    Id = r,
                    Succeeded = true,
                    ErrorMessage = string.Empty,
                }).ToArray(),
            };
        }

        protected virtual string GetIndexName(string documentType)
        {
            // Use different index for each document type
            return string.Join("-", _searchOptions.Scope, documentType).ToLowerInvariant();
        }

        protected virtual async Task<bool> IndexExistsAsync(string indexName)
        {
            var indexes = await Client.ListIndicesAsync();
            return indexes.Items.Count > 0 && indexes.Items.Exists(x => x.Name.EqualsInvariant(indexName));
        }

        protected virtual void ThrowException(string message, Exception innerException)
        {
            throw new SearchException($"{message}. Search service name: {_algoliaSearchOptions.AppId}, Scope: {_searchOptions.Scope}", innerException);
        }


        protected virtual SearchClient CreateSearchServiceClient()
        {
            var result = new SearchClient(_algoliaSearchOptions.AppId, _algoliaSearchOptions.ApiKey);
            return result;
        }
    }
}
