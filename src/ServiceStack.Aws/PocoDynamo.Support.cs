﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using ServiceStack.Text;

namespace ServiceStack.Aws
{
    public partial class PocoDynamo
    {
        //Error Handling: http://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ErrorHandling.html
        public void Exec(Action fn)
        {
            Exec(() => {
                fn();
                return true;
            });
        }

        public T Exec<T>(Func<T> fn)
        {
            var i = 0;
            Exception originalEx = null;
            var firstAttempt = DateTime.UtcNow;

            bool retry = false;

            while (DateTime.UtcNow - firstAttempt < MaxRetryOnExceptionTimeout)
            {
                i++;
                try
                {
                    return fn();
                }
                catch (Exception outerEx)
                {
                    var ex = outerEx.UnwrapIfSingleException();

                    if (originalEx == null)
                        originalEx = ex;

                    var amazonEx = ex as AmazonDynamoDBException;
                    if (amazonEx != null)
                    {
                        if (amazonEx.StatusCode == HttpStatusCode.BadRequest &&
                            !RetryOnErrorCodes.Contains(amazonEx.ErrorCode))
                            throw;
                    }

                    SleepBackOffMultiplier(i);
                }
            }

            throw new TimeoutException("Exceeded timeout of {0}".Fmt(MaxRetryOnExceptionTimeout), originalEx);
        }

        private static void SleepBackOffMultiplier(int i)
        {
            var nextTryMs = (2 ^ i) * 50;
            Thread.Sleep(nextTryMs);
        }

        public bool WaitForTablesToBeReady(List<string> tableNames, TimeSpan? timeout = null)
        {
            if (tableNames.Count == 0)
                return true;

            var pendingTables = new List<string>(tableNames);

            var startAt = DateTime.UtcNow;
            do
            {
                try
                {
                    var responses = pendingTables.Map(x =>
                        Exec(() => DynamoDb.DescribeTable(x)));

                    foreach (var response in responses)
                    {
                        if (response.Table.TableStatus == DynamoStatus.Active)
                            pendingTables.Remove(response.Table.TableName);
                    }

                    if (Log.IsDebugEnabled)
                        Log.Debug("Tables Pending: {0}".Fmt(AwsClientUtils.ToJsv(pendingTables)));

                    if (pendingTables.Count == 0)
                        return true;

                    if (timeout != null && DateTime.UtcNow - startAt > timeout.Value)
                        return false;

                    Thread.Sleep(PollTableStatus);
                }
                catch (ResourceNotFoundException)
                {
                    // DescribeTable is eventually consistent. So you might
                    // get resource not found. So we handle the potential exception.
                }
            } while (true);
        }

        public bool WaitForTablesToBeRemoved(List<string> tableNames, TimeSpan? timeout = null)
        {
            if (tableNames.Count == 0)
                return true;

            var pendingTables = new List<string>(tableNames);

            var startAt = DateTime.UtcNow;
            do
            {
                var existingTables = GetTableNames();
                pendingTables.RemoveAll(x => !existingTables.Contains(x));

                if (Log.IsDebugEnabled)
                    Log.DebugFormat("Waiting for Tables to be removed: {0}", pendingTables.Dump());

                if (pendingTables.Count == 0)
                    return true;

                if (timeout != null && DateTime.UtcNow - startAt > timeout.Value)
                    return false;

                Thread.Sleep(PollTableStatus);

            } while (true);
        }
    }
}