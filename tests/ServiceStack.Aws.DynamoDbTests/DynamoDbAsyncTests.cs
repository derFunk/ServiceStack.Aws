﻿using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using ServiceStack.Aws.DynamoDb;
using ServiceStack.Aws.DynamoDbTests.Shared;

namespace ServiceStack.Aws.DynamoDbTests
{
    [TestFixture]
    public class DynamoDbAsyncTests : DynamoTestBase
    {
        [Test]
        public async Task Can_create_Tables_Async()
        {
            var db = CreatePocoDynamo()
                .RegisterTable<Poco>();

            db.DeleteTable<Poco>();

            var tables = db.GetTableNames().ToList();
            Assert.That(!tables.Contains(typeof(Poco).Name));

            await db.InitSchemaAsync();

            tables = db.GetTableNames().ToList();
            Assert.That(tables.Contains(typeof(Poco).Name));
        }
    }
}