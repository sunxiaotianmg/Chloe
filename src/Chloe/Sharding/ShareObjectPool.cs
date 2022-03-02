﻿using System.Threading.Tasks;

namespace Chloe.Sharding
{
    static class ShareObjectPoolExtension
    {
        public static async Task<IPoolItem<T>> GetOne<T>(this IShareObjectPool<T> pool, bool @async)
        {
            if (async)
            {
                return await pool.GetAsync();
            }

            return pool.Get();
        }
    }


    internal interface IShareObjectPool<T> : IDisposable
    {
        int Size { get; }
        Task<IPoolItem<T>> GetAsync();
        IPoolItem<T> Get();
    }

    internal interface IPoolItem<T> : IDisposable
    {
        public T Resource { get; }
    }

    interface IShareDbContextPool : IShareObjectPool<IDbContext>
    {

    }

    class ShareObjectPool<T> : IShareObjectPool<T>
    {
        bool _disposed;
        List<T> All;
        Queue<T> Stocks;
        Queue<TaskCompletionSource<T>> Waitings;

        public ShareObjectPool(List<T> objects)
        {
            this.All = objects;
            this.Stocks = new Queue<T>(objects);
            this.Waitings = new Queue<TaskCompletionSource<T>>();
        }

        public int Size { get { return this.All.Count; } }

        public void Dispose()
        {
            if (this._disposed)
                return;

            if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
            {
                foreach (var obj in this.All)
                {
                    (obj as IDisposable)?.Dispose();
                }
            }

            ObjectDisposedException objectDisposedException = new ObjectDisposedException(this.GetType().FullName);
            foreach (var waiting in this.Waitings)
            {
                waiting.TrySetException(objectDisposedException);
            }

            this._disposed = true;
        }

        public async Task<IPoolItem<T>> GetAsync()
        {
            if (this._disposed)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }

            TaskCompletionSource<T> tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this)
            {
                this.Waitings.Enqueue(tcs);
                this.TryEmit();
            }

            var resource = await tcs.Task;
            return new PoolItem(resource, this);
        }

        public IPoolItem<T> Get()
        {
            return this.GetAsync().GetAwaiter().GetResult();
        }

        void Return(T obj)
        {
            lock (this)
            {
                this.Stocks.Enqueue(obj);
                this.TryEmit();
            }
        }

        void TryEmit()
        {
            if (this._disposed)
                return;

            if (!(this.Stocks.Count > 0 && this.Waitings.Count > 0))
            {
                return;
            }

            T obj = this.Stocks.Dequeue();
            var tcs = this.Waitings.Dequeue();
            tcs.TrySetResult(obj);
        }

        class PoolItem : IPoolItem<T>
        {
            ShareObjectPool<T> Pool;
            bool _disposed;

            public PoolItem(T resource, ShareObjectPool<T> pool)
            {
                this.Resource = resource;
                this.Pool = pool;
            }

            public T Resource { get; private set; }

            public void Dispose()
            {
                if (this._disposed)
                    return;

                this.Pool.Return(this.Resource);
                this._disposed = true;
            }
        }
    }

    class ShareDbContextPool : ShareObjectPool<IDbContext>, IShareDbContextPool
    {
        public ShareDbContextPool(List<IDbContext> dbContexts) : base(dbContexts)
        {

        }
    }
}