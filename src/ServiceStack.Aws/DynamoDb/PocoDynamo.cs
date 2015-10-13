﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using ServiceStack.Aws.Support;
using ServiceStack.Logging;

namespace ServiceStack.Aws.DynamoDb
{
    public interface IPocoDynamo : IRequiresSchema
    {
        IAmazonDynamoDB DynamoDb { get; }
        ISequenceSource Sequences { get; }
        DynamoConverters Converters { get; }
        TimeSpan MaxRetryOnExceptionTimeout { get; }

        Table GetTableSchema(Type table);
        DynamoMetadataType GetTableMetadata(Type table);
        List<string> GetTableNames();
        bool CreateMissingTables(IEnumerable<DynamoMetadataType> tables, TimeSpan? timeout = null);
        bool DeleteAllTables(TimeSpan? timeout = null);
        bool DeleteTables(IEnumerable<string> tableNames, TimeSpan? timeout = null);
        T GetItem<T>(object hash);
        T GetItem<T>(object hash, object range);
        List<T> GetItems<T>(IEnumerable<object> hashes);
        T PutItem<T>(T value, bool returnOld = false);
        void PutItems<T>(IEnumerable<T> items);
        T DeleteItem<T>(object hash, ReturnItem returnItem = ReturnItem.None);
        void DeleteItems<T>(IEnumerable<object> hashes);
        long Increment<T>(object hash, string fieldName, long amount = 1);
        bool WaitForTablesToBeReady(IEnumerable<string> tableNames, TimeSpan? timeout = null);
        bool WaitForTablesToBeDeleted(IEnumerable<string> tableNames, TimeSpan? timeout = null);

        void PutRelated<T>(object hash, IEnumerable<T> items);
        IEnumerable<T> GetRelated<T>(object hash);

        IEnumerable<T> ScanAll<T>();

        ScanExpression<T> FromScan<T>(Expression<Func<T, bool>> filterExpression = null);
        List<T> Scan<T>(ScanExpression<T> request, int limit);
        IEnumerable<T> Scan<T>(ScanExpression<T> request);
        ScanExpression<T> FromScanIndex<T>(Expression<Func<T, bool>> filterExpression = null);

        List<T> Scan<T>(ScanRequest request, int limit);
        IEnumerable<T> Scan<T>(ScanRequest request);
        IEnumerable<T> Scan<T>(ScanRequest request, Func<ScanResponse, IEnumerable<T>> converter);

        QueryExpression<T> FromQuery<T>(Expression<Func<T, bool>> keyExpression = null);
        List<T> Query<T>(QueryExpression<T> request, int limit);
        IEnumerable<T> Query<T>(QueryExpression<T> request);
        QueryExpression<T> FromQueryIndex<T>(Expression<Func<T, bool>> keyExpression = null);

        List<T> Query<T>(QueryRequest request, int limit);
        IEnumerable<T> Query<T>(QueryRequest request);
        IEnumerable<T> Query<T>(QueryRequest request, Func<QueryResponse, IEnumerable<T>> converter);

        IPocoDynamo ClientWith(
            bool? consistentRead = null,
            long? readCapacityUnits = null,
            long? writeCapacityUnits = null,
            TimeSpan? pollTableStatus = null,
            TimeSpan? maxRetryOnExceptionTimeout = null,
            int? limit = null,
            bool? scanIndexForward = null);

        void Close();
    }

    public partial class PocoDynamo : IPocoDynamo
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PocoDynamo));

        public IAmazonDynamoDB DynamoDb { get; private set; }

        public ISequenceSource Sequences { get; set; }

        public DynamoConverters Converters { get; set; }

        public bool ConsistentRead { get; set; }

        public bool ScanIndexForward { get; set; }

        /// <summary>
        /// If the client needs to delete/re-create the DynamoDB table, this is the Read Capacity to use
        /// </summary>
        public long ReadCapacityUnits { get; set; }

        /// <summary>
        /// If the client needs to delete/re-create the DynamoDB table, this is the Write Capacity to use
        /// </summary> 
        public long WriteCapacityUnits { get; set; }

        public int PagingLimit { get; set; }

        public HashSet<string> RetryOnErrorCodes { get; set; }

        public TimeSpan PollTableStatus { get; set; }

        public TimeSpan MaxRetryOnExceptionTimeout { get; set; }

        public PocoDynamo(IAmazonDynamoDB dynamoDb)
        {
            this.DynamoDb = dynamoDb;
            this.Sequences = new DynamoDbSequenceSource(this);
            this.Converters = DynamoMetadata.Converters;
            PollTableStatus = TimeSpan.FromSeconds(2);
            MaxRetryOnExceptionTimeout = TimeSpan.FromSeconds(60);
            ReadCapacityUnits = 10;
            WriteCapacityUnits = 5;
            ConsistentRead = true;
            ScanIndexForward = true;
            PagingLimit = 1000;
            RetryOnErrorCodes = new HashSet<string> {
                "ThrottlingException",
                "ProvisionedThroughputExceededException",
                "LimitExceededException",
                "ResourceInUseException",
            };
        }

        public void InitSchema()
        {
            CreateMissingTables(DynamoMetadata.GetTables());
            Sequences.InitSchema();
        }

        public IPocoDynamo ClientWith(
            bool? consistentRead = null,
            long? readCapacityUnits = null,
            long? writeCapacityUnits = null,
            TimeSpan? pollTableStatus = null,
            TimeSpan? maxRetryOnExceptionTimeout = null,
            int? limit = null,
            bool? scanIndexForward = null)
        {
            return new PocoDynamo(DynamoDb)
            {
                ConsistentRead = consistentRead ?? ConsistentRead,
                ReadCapacityUnits = readCapacityUnits ?? ReadCapacityUnits,
                WriteCapacityUnits = writeCapacityUnits ?? WriteCapacityUnits,
                PollTableStatus = pollTableStatus ?? PollTableStatus,
                MaxRetryOnExceptionTimeout = maxRetryOnExceptionTimeout ?? MaxRetryOnExceptionTimeout,
                PagingLimit = limit ?? PagingLimit,
                ScanIndexForward = scanIndexForward ?? ScanIndexForward,
                Converters = Converters,
                Sequences = Sequences,
                RetryOnErrorCodes = new HashSet<string>(RetryOnErrorCodes),
            };
        }

        public DynamoMetadataType GetTableMetadata(Type table)
        {
            return DynamoMetadata.GetTable(table);
        }

        public List<string> GetTableNames()
        {
            var to = new List<string>();

            ListTablesResponse response = null;
            do
            {
                response = response == null
                    ? Exec(() => DynamoDb.ListTables())
                    : Exec(() => DynamoDb.ListTables(response.LastEvaluatedTableName));

                to.AddRange(response.TableNames);
            } while (response.LastEvaluatedTableName != null);

            return to;
        }

        readonly Type[] throwNotFoundExceptions = {
            typeof(ResourceNotFoundException)
        };

        public Table GetTableSchema(Type type)
        {
            var table = DynamoMetadata.GetTable(type);
            return Exec(() =>
            {
                try
                {
                    Table awsTable;
                    Table.TryLoadTable(DynamoDb, table.Name, out awsTable);
                    return awsTable;
                }
                catch (ResourceNotFoundException)
                {
                    return null;
                }
            }, throwNotFoundExceptions);
        }

        public bool CreateMissingTables(IEnumerable<DynamoMetadataType> tables, TimeSpan? timeout = null)
        {
            var tablesList = tables.Safe().ToList();
            if (tablesList.Count == 0)
                return true;

            var existingTableNames = GetTableNames();

            foreach (var table in tablesList)
            {
                if (existingTableNames.Contains(table.Name))
                    continue;

                if (Log.IsDebugEnabled)
                    Log.Debug("Creating Table: " + table.Name);

                var request = ToCreateTableRequest(table);
                Exec(() =>
                {
                    try
                    {
                        DynamoDb.CreateTable(request);
                    }
                    catch (AmazonDynamoDBException ex)
                    {
                        const string TableAlreadyExists = "ResourceInUseException";
                        if (ex.ErrorCode == TableAlreadyExists)
                            return;

                        throw;
                    }
                });
            }

            return WaitForTablesToBeReady(tablesList.Map(x => x.Name), timeout);
        }

        protected virtual CreateTableRequest ToCreateTableRequest(DynamoMetadataType table)
        {
            var props = table.Type.GetSerializableProperties();
            if (props.Length == 0)
                throw new NotSupportedException("{0} does not have any serializable properties".Fmt(table.Name));

            var keySchema = new List<KeySchemaElement> {
                new KeySchemaElement(table.HashKey.Name, KeyType.HASH),
            };
            var attrDefinitions = new List<AttributeDefinition> {
                new AttributeDefinition(table.HashKey.Name, table.HashKey.DbType),
            };
            if (table.RangeKey != null)
            {
                keySchema.Add(new KeySchemaElement(table.RangeKey.Name, KeyType.RANGE));
                attrDefinitions.Add(new AttributeDefinition(table.RangeKey.Name, table.RangeKey.DbType));
            }

            var to = new CreateTableRequest
            {
                TableName = table.Name,
                KeySchema = keySchema,
                AttributeDefinitions = attrDefinitions,
                ProvisionedThroughput = new ProvisionedThroughput
                {
                    ReadCapacityUnits = table.ReadCapacityUnits ?? ReadCapacityUnits,
                    WriteCapacityUnits = table.WriteCapacityUnits ?? WriteCapacityUnits,
                }
            };

            if (!table.LocalIndexes.IsEmpty())
            {
                to.LocalSecondaryIndexes = table.LocalIndexes.Map(x => new LocalSecondaryIndex
                {
                    IndexName = x.Name,
                    KeySchema = x.ToKeySchemas(),
                    Projection = new Projection
                    {
                        ProjectionType = x.ProjectionType,
                        NonKeyAttributes = x.ProjectedFields.Safe().ToList(),
                    },
                });

                table.LocalIndexes.Each(x =>
                {
                    if (x.RangeKey != null && attrDefinitions.All(a => a.AttributeName != x.RangeKey.Name))
                        attrDefinitions.Add(new AttributeDefinition(x.RangeKey.Name, x.RangeKey.DbType));
                });
            }
            if (!table.GlobalIndexes.IsEmpty())
            {
                to.GlobalSecondaryIndexes = table.GlobalIndexes.Map(x => new GlobalSecondaryIndex
                {
                    IndexName = x.Name,
                    KeySchema = x.ToKeySchemas(),
                    Projection = new Projection
                    {
                        ProjectionType = x.ProjectionType,
                        NonKeyAttributes = x.ProjectedFields.Safe().ToList(),
                    },
                    ProvisionedThroughput = new ProvisionedThroughput
                    {
                        ReadCapacityUnits = x.ReadCapacityUnits ?? ReadCapacityUnits,
                        WriteCapacityUnits = x.WriteCapacityUnits ?? WriteCapacityUnits,
                    }
                });

                table.GlobalIndexes.Each(x =>
                {
                    if (x.HashKey != null && attrDefinitions.All(a => a.AttributeName != x.HashKey.Name))
                        attrDefinitions.Add(new AttributeDefinition(x.HashKey.Name, x.HashKey.DbType));
                    if (x.RangeKey != null && attrDefinitions.All(a => a.AttributeName != x.RangeKey.Name))
                        attrDefinitions.Add(new AttributeDefinition(x.RangeKey.Name, x.RangeKey.DbType));
                });
            }


            return to;
        }

        public bool DeleteAllTables(TimeSpan? timeout = null)
        {
            return DeleteTables(GetTableNames(), timeout);
        }

        public bool DeleteTables(IEnumerable<string> tableNames, TimeSpan? timeout = null)
        {
            foreach (var tableName in tableNames)
            {
                Exec(() => DynamoDb.DeleteTable(new DeleteTableRequest(tableName)));
            }

            return WaitForTablesToBeDeleted(tableNames);
        }

        public T GetItem<T>(object hash)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new GetItemRequest
            {
                TableName = table.Name,
                Key = Converters.ToAttributeKeyValue(this, table.HashKey, hash),
                ConsistentRead = ConsistentRead,
            };

            var response = Exec(() => DynamoDb.GetItem(request), throwNotFoundExceptions);

            if (!response.IsItemSet)
                return default(T);
            var attributeValues = response.Item;

            return Converters.FromAttributeValues<T>(table, attributeValues);
        }

        const int MaxReadBatchSize = 100;

        public List<T> GetItems<T>(IEnumerable<object> hashes)
        {
            var to = new List<T>();

            var table = DynamoMetadata.GetTable<T>();
            var remainingIds = hashes.ToList();

            while (remainingIds.Count > 0)
            {
                var batchSize = Math.Min(remainingIds.Count, MaxReadBatchSize);
                var nextBatch = remainingIds.GetRange(0, batchSize);
                remainingIds.RemoveRange(0, batchSize);

                var getItems = new KeysAndAttributes
                {
                    ConsistentRead = ConsistentRead,
                };
                nextBatch.Each(id =>
                    getItems.Keys.Add(Converters.ToAttributeKeyValue(this, table.HashKey, id)));

                var request = new BatchGetItemRequest(new Dictionary<string, KeysAndAttributes> {
                    { table.Name, getItems }
                });

                var response = Exec(() => DynamoDb.BatchGetItem(request));

                List<Dictionary<string, AttributeValue>> results;
                if (response.Responses.TryGetValue(table.Name, out results))
                    results.Each(x => to.Add(Converters.FromAttributeValues<T>(table, x)));

                var i = 0;
                while (response.UnprocessedKeys.Count > 0)
                {
                    response = Exec(() => DynamoDb.BatchGetItem(response.UnprocessedKeys));
                    if (response.Responses.TryGetValue(table.Name, out results))
                        results.Each(x => to.Add(Converters.FromAttributeValues<T>(table, x)));

                    if (response.UnprocessedKeys.Count > 0)
                        i.SleepBackOffMultiplier();
                }
            }

            return to;
        }

        public T GetItem<T>(object hash, object range)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new GetItemRequest
            {
                TableName = table.Name,
                Key = Converters.ToAttributeKeyValue(this, table, hash, range),
                ConsistentRead = ConsistentRead,
            };

            var response = Exec(() => DynamoDb.GetItem(request), throwNotFoundExceptions);

            if (!response.IsItemSet)
                return default(T);
            var attributeValues = response.Item;

            return DynamoMetadata.Converters.FromAttributeValues<T>(table, attributeValues);
        }

        public IEnumerable<T> GetRelated<T>(object hash)
        {
            var table = DynamoMetadata.GetTable<T>();

            var argType = hash.GetType();
            var dbType = Converters.GetFieldType(argType);
            var request = new QueryRequest(table.Name)
            {
                Limit = PagingLimit,
                KeyConditionExpression = "{0} = :k1".Fmt(table.HashKey.Name),
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    {":k1", Converters.ToAttributeValue(this, argType, dbType, hash) }
                }
            };

            return Query(request, r => r.ConvertAll<T>());
        }

        public T PutItem<T>(T value, bool returnOld = false)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new PutItemRequest
            {
                TableName = table.Name,
                Item = Converters.ToAttributeValues(this, value, table),
                ReturnValues = returnOld ? ReturnValue.ALL_OLD : ReturnValue.NONE,
            };

            var response = Exec(() => DynamoDb.PutItem(request));

            if (response.Attributes.IsEmpty())
                return default(T);

            return Converters.FromAttributeValues<T>(table, response.Attributes);
        }

        public void PutRelated<T>(object hash, IEnumerable<T> items)
        {
            var table = DynamoMetadata.GetTable<T>();

            if (table.HashKey == null || table.RangeKey == null)
                throw new ArgumentException("Related table '{0}' needs both a HashKey and RangeKey".Fmt(typeof(T).Name));

            var related = items.ToList();
            related.Each(x => table.HashKey.SetValue(x, hash));

            PutItems(related);
        }

        const int MaxWriteBatchSize = 25;

        public void PutItems<T>(IEnumerable<T> items)
        {
            var table = DynamoMetadata.GetTable<T>();
            var remaining = items.ToList();

            while (remaining.Count > 0)
            {
                var batchSize = Math.Min(remaining.Count, MaxWriteBatchSize);
                var nextBatch = remaining.GetRange(0, batchSize);
                remaining.RemoveRange(0, batchSize);

                var putItems = nextBatch.Map(x => new WriteRequest(
                    new PutRequest(Converters.ToAttributeValues(this, x, table))));

                var request = new BatchWriteItemRequest(new Dictionary<string, List<WriteRequest>> {
                    { table.Name, putItems }
                });

                var response = Exec(() => DynamoDb.BatchWriteItem(request));

                var i = 0;
                while (response.UnprocessedItems.Count > 0)
                {
                    response = Exec(() => DynamoDb.BatchWriteItem(response.UnprocessedItems));

                    if (response.UnprocessedItems.Count > 0)
                        i.SleepBackOffMultiplier();
                }
            }
        }

        public T DeleteItem<T>(object hash, ReturnItem returnItem = ReturnItem.None)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new DeleteItemRequest
            {
                TableName = table.Name,
                Key = Converters.ToAttributeKeyValue(this, table.HashKey, hash),
                ReturnValues = returnItem.ToReturnValue(),
            };

            var response = Exec(() => DynamoDb.DeleteItem(request));

            if (response.Attributes.IsEmpty())
                return default(T);

            return Converters.FromAttributeValues<T>(table, response.Attributes);
        }

        public void DeleteItems<T>(IEnumerable<object> hashes)
        {
            var table = DynamoMetadata.GetTable<T>();
            var remainingIds = hashes.ToList();

            while (remainingIds.Count > 0)
            {
                var batchSize = Math.Min(remainingIds.Count, MaxWriteBatchSize);
                var nextBatch = remainingIds.GetRange(0, batchSize);
                remainingIds.RemoveRange(0, batchSize);

                var deleteItems = nextBatch.Map(id => new WriteRequest(
                    new DeleteRequest(Converters.ToAttributeKeyValue(this, table.HashKey, id))));

                var request = new BatchWriteItemRequest(new Dictionary<string, List<WriteRequest>> {
                    { table.Name, deleteItems }
                });

                var response = Exec(() => DynamoDb.BatchWriteItem(request));

                var i = 0;
                while (response.UnprocessedItems.Count > 0)
                {
                    response = Exec(() => DynamoDb.BatchWriteItem(response.UnprocessedItems));

                    if (response.UnprocessedItems.Count > 0)
                        i.SleepBackOffMultiplier();
                }
            }
        }

        public long Increment<T>(object hash, string fieldName, long amount = 1)
        {
            var type = DynamoMetadata.GetType<T>();
            var request = new UpdateItemRequest
            {
                TableName = type.Name,
                Key = Converters.ToAttributeKeyValue(this, type.HashKey, hash),
                AttributeUpdates = new Dictionary<string, AttributeValueUpdate> {
                    {
                        fieldName,
                        new AttributeValueUpdate {
                            Action = AttributeAction.ADD,
                            Value = new AttributeValue { N = amount.ToString() }
                        }
                    }
                },
                ReturnValues = ReturnValue.ALL_NEW,
            };

            var response = DynamoDb.UpdateItem(request);

            return response.Attributes.Count > 0
                ? Convert.ToInt64(response.Attributes[fieldName].N)
                : 0;
        }

        public IEnumerable<T> ScanAll<T>()
        {
            var type = DynamoMetadata.GetType<T>();
            var request = new ScanRequest
            {
                Limit = PagingLimit,
                TableName = type.Name,
            };

            return Scan(request, r => r.ConvertAll<T>());
        }

        public IEnumerable<T> Scan<T>(ScanRequest request, Func<ScanResponse, IEnumerable<T>> converter)
        {
            ScanResponse response = null;
            do
            {
                if (response != null)
                    request.ExclusiveStartKey = response.LastEvaluatedKey;

                response = Exec(() => DynamoDb.Scan(request));

                var results = converter(response);

                foreach (var result in results)
                {
                    yield return result;
                }

            } while (!response.LastEvaluatedKey.IsEmpty());
        }

        public ScanExpression<T> FromScan<T>(Expression<Func<T, bool>> filterExpression = null)
        {
            var q = new ScanExpression<T>(this)
            {
                Limit = PagingLimit,
                ConsistentRead = !typeof(T).IsGlobalIndex() && this.ConsistentRead,
            };

            if (filterExpression != null)
                q.Filter(filterExpression);

            return q;
        }

        public ScanExpression<T> FromScanIndex<T>(Expression<Func<T, bool>> filterExpression = null)
        {
            var table = typeof(T).GetIndexTable();
            var index = table.GetIndex(typeof(T));
            var q = new ScanExpression<T>(this, table)
            {
                IndexName = index.Name,
                Limit = PagingLimit,
                ConsistentRead = !typeof(T).IsGlobalIndex() && this.ConsistentRead,
            };

            if (filterExpression != null)
                q.Filter(filterExpression);

            return q;
        }

        public List<T> Scan<T>(ScanExpression<T> request, int limit)
        {
            var to = new List<T>();

            if (request.Limit == default(int))
                request.Limit = limit;

            ScanResponse response = null;
            do
            {
                if (response != null)
                    request.ExclusiveStartKey = response.LastEvaluatedKey;

                response = Exec(() => DynamoDb.Scan(request));
                var results = response.ConvertAll<T>();

                foreach (var result in results)
                {
                    to.Add(result);

                    if (to.Count >= limit)
                        break;
                }

            } while (!response.LastEvaluatedKey.IsEmpty() && to.Count < limit);

            return to;
        }

        public IEnumerable<T> Scan<T>(ScanExpression<T> request)
        {
            return Scan(request, r => r.ConvertAll<T>());
        }

        public List<T> Scan<T>(ScanRequest request, int limit)
        {
            var to = new List<T>();

            if (request.Limit == default(int))
                request.Limit = limit;

            ScanResponse response = null;
            do
            {
                if (response != null)
                    request.ExclusiveStartKey = response.LastEvaluatedKey;

                response = Exec(() => DynamoDb.Scan(request));
                var results = response.ConvertAll<T>();

                foreach (var result in results)
                {
                    to.Add(result);

                    if (to.Count >= limit)
                        break;
                }

            } while (!response.LastEvaluatedKey.IsEmpty() && to.Count < limit);

            return to;
        }

        public IEnumerable<T> Scan<T>(ScanRequest request)
        {
            return Scan(request, r => r.ConvertAll<T>());
        }

        public QueryExpression<T> FromQuery<T>(Expression<Func<T, bool>> keyExpression = null)
        {
            var q = new QueryExpression<T>(this)
            {
                Limit = PagingLimit,
                ConsistentRead = !typeof(T).IsGlobalIndex() && this.ConsistentRead,
                ScanIndexForward = this.ScanIndexForward,
            };

            if (keyExpression != null)
                q.KeyCondition(keyExpression);

            return q;
        }

        public QueryExpression<T> FromQueryIndex<T>(Expression<Func<T, bool>> keyExpression = null)
        {
            var table = typeof(T).GetIndexTable();
            var index = table.GetIndex(typeof(T));
            var q = new QueryExpression<T>(this, table)
            {
                IndexName = index.Name,
                Limit = PagingLimit,
                ConsistentRead = !typeof(T).IsGlobalIndex() && this.ConsistentRead,
                ScanIndexForward = this.ScanIndexForward,
            };

            if (keyExpression != null)
                q.KeyCondition(keyExpression);

            return q;
        }

        public IEnumerable<T> Query<T>(QueryExpression<T> request)
        {
            return Query(request, r => r.ConvertAll<T>());
        }

        public List<T> Query<T>(QueryExpression<T> request, int limit)
        {
            return Query<T>((QueryRequest)request, limit);
        }

        public IEnumerable<T> Query<T>(QueryRequest request)
        {
            return Query(request, r => r.ConvertAll<T>());
        }

        public List<T> Query<T>(QueryRequest request, int limit)
        {
            var to = new List<T>();

            if (request.Limit == default(int))
                request.Limit = limit;

            QueryResponse response = null;
            do
            {
                if (response != null)
                    request.ExclusiveStartKey = response.LastEvaluatedKey;

                response = Exec(() => DynamoDb.Query(request));
                var results = response.ConvertAll<T>();

                foreach (var result in results)
                {
                    to.Add(result);

                    if (to.Count >= limit)
                        break;
                }

            } while (!response.LastEvaluatedKey.IsEmpty() && to.Count < limit);

            return to;
        }

        public IEnumerable<T> Query<T>(QueryRequest request, Func<QueryResponse, IEnumerable<T>> converter)
        {
            QueryResponse response = null;
            do
            {
                if (response != null)
                    request.ExclusiveStartKey = response.LastEvaluatedKey;

                response = Exec(() => DynamoDb.Query(request));
                var results = converter(response);

                foreach (var result in results)
                {
                    yield return result;
                }

            } while (!response.LastEvaluatedKey.IsEmpty());
        }

        public void Close()
        {
            if (DynamoDb == null)
                return;

            DynamoDb.Dispose();
            DynamoDb = null;
        }
    }
}