﻿using Chloe.Sharding.Queries;
using System.Threading;

namespace Chloe.Sharding.Enumerables
{
    class PagingQueryEnumerable : FeatureEnumerable<PagingResult>
    {
        ShardingQueryPlan _queryPlan;

        public PagingQueryEnumerable(ShardingQueryPlan queryPlan)
        {
            this._queryPlan = queryPlan;
        }

        public override IFeatureEnumerator<PagingResult> GetFeatureEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator(this, cancellationToken);
        }

        class Enumerator : FeatureEnumerator<PagingResult>
        {
            PagingQueryEnumerable _enumerable;
            CancellationToken _cancellationToken;
            public Enumerator(PagingQueryEnumerable enumerable, CancellationToken cancellationToken)
            {
                this._enumerable = enumerable;
                this._cancellationToken = cancellationToken;
            }

            protected override async Task<IFeatureEnumerator<PagingResult>> CreateEnumerator(bool @async)
            {
                ShardingQueryPlan queryPlan = this._enumerable._queryPlan;
                var countQuery = this.GetCountQuery(queryPlan);

                List<QueryResult<long>> routeTableCounts = await countQuery.ToListAsync();
                long totals = routeTableCounts.Select(a => a.Result).Sum();

                List<object> dataList = null;

                if (queryPlan.IsOrderedTables)
                {
                    OrderedTableQuery orderedTableQuery = new OrderedTableQuery(queryPlan, routeTableCounts);
                    dataList = await orderedTableQuery.ToListAsync(this._cancellationToken);
                }
                else
                {
                    OrdinaryQueryEnumerable ordinaryQuery = new OrdinaryQueryEnumerable(queryPlan);
                    dataList = await ordinaryQuery.ToListAsync(this._cancellationToken);
                }

                return new ScalarFeatureEnumerator<PagingResult>(new PagingResult() { Totals = totals, DataList = dataList });
            }

            AggregateQuery<long> GetCountQuery(ShardingQueryPlan queryPlan)
            {
                Func<IQuery, bool, Task<long>> executor = async (query, @async) =>
                {
                    long result = @async ? await query.LongCountAsync() : query.LongCount();
                    return result;
                };

                var aggQuery = new AggregateQuery<long>(queryPlan, executor);
                return aggQuery;
            }
        }
    }

    class PagingResultDataListEnumerable : FeatureEnumerable<object>
    {
        PagingQueryEnumerable _pagingQuery;

        public PagingResultDataListEnumerable(PagingQueryEnumerable pagingQuery)
        {
            this._pagingQuery = pagingQuery;
        }

        public override IFeatureEnumerator<object> GetFeatureEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator(this, cancellationToken);
        }

        class Enumerator : FeatureEnumerator<object>
        {
            PagingResultDataListEnumerable _enumerable;
            CancellationToken _cancellationToken;
            public Enumerator(PagingResultDataListEnumerable enumerable, CancellationToken cancellationToken)
            {
                this._enumerable = enumerable;
                this._cancellationToken = cancellationToken;
            }

            protected override async Task<IFeatureEnumerator<object>> CreateEnumerator(bool @async)
            {
                PagingResult pagingResult = await this._enumerable._pagingQuery.FirstAsync(this._cancellationToken);
                return new FeatureEnumeratorAdapter<object>(pagingResult.DataList.GetEnumerator());
            }
        }
    }
}
