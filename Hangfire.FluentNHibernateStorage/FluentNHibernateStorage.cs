﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Transactions;
using FluentNHibernate.Cfg;
using FluentNHibernate.Cfg.Db;
using Hangfire.Annotations;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.FluentNHibernateStorage.JobQueue;
using Hangfire.FluentNHibernateStorage.Monitoring;
using Hangfire.Logging;
using Hangfire.Server;
using Hangfire.Storage;
using NHibernate;
using NHibernate.Tool.hbm2ddl;

namespace Hangfire.FluentNHibernateStorage
{
    public class FluentNHibernateStorage : JobStorage, IDisposable
    {
        private static readonly ILog Logger = LogProvider.GetLogger(typeof(FluentNHibernateStorage));


        private readonly FluentNHibernateStorageOptions _options;

        private readonly Dictionary<IPersistenceConfigurer, ISessionFactory> _sessionFactories =
            new Dictionary<IPersistenceConfigurer, ISessionFactory>();

        protected IPersistenceConfigurer _configurer;

        private bool _testBuild;


        public FluentNHibernateStorage(IPersistenceConfigurer pcf)
            : this(pcf, new FluentNHibernateStorageOptions())
        {
        }

        public FluentNHibernateStorage(IPersistenceConfigurer pcf, FluentNHibernateStorageOptions options)
        {
            ConfigurerFunc = () => { return pcf; };


            _options = options ?? new FluentNHibernateStorageOptions();

            InitializeQueueProviders();
        }


        public virtual PersistentJobQueueProviderCollection QueueProviders { get; private set; }

        public Func<IPersistenceConfigurer> ConfigurerFunc { get; set; }

        public void Dispose()
        {
        }


        private void InitializeQueueProviders()
        {
            QueueProviders =
                new PersistentJobQueueProviderCollection(
                    new FluentNHibernateJobQueueProvider(this, _options));
        }

        public override IEnumerable<IServerComponent> GetComponents()
        {
            yield return new ExpirationManager(this, _options.JobExpirationCheckInterval);
            yield return new CountersAggregator(this, _options.CountersAggregateInterval);
        }

        public override void WriteOptionsToLog(ILog logger)
        {
            logger.Info("Using the following options for SQL Server job storage:");
            logger.InfoFormat("    Queue poll interval: {0}.", _options.QueuePollInterval);
        }


        public override IMonitoringApi GetMonitoringApi()
        {
            return new FluentNHibernateMonitoringApi(this, _options.DashboardJobListLimit);
        }

        public override IStorageConnection GetConnection()
        {
            return new FluentNHibernateStorageConnection(this);
        }

        internal void UseTransactionStateless([InstantHandle] Action<IStatelessSession> action)
        {
            UseTransactionStateless(session =>
            {
                action(session);
                return true;
            }, null);
        }

        internal T UseTransactionStateless<T>(
            [InstantHandle] Func<IStatelessSession, T> func, IsolationLevel? isolationLevel)
        {
            using (var transaction = CreateTransaction(isolationLevel ?? _options.TransactionIsolationLevel))
            {
                var result = UseStatelessSession(func);
                transaction.Complete();

                return result;
            }
        }

        internal void UseStatefulTransaction([InstantHandle] Action<ISession> action)
        {
            UseStatefulTransaction(session =>
            {
                action(session);
                return true;
            }, null);
        }

        internal T UseStatefulTransaction<T>(
            [InstantHandle] Func<ISession, T> func, IsolationLevel? isolationLevel)
        {
            using (var transaction = CreateTransaction(isolationLevel ?? _options.TransactionIsolationLevel))
            {
                var result = UseSession(func);
                transaction.Complete();

                return result;
            }
        }

        private TransactionScope CreateTransaction(IsolationLevel? isolationLevel)
        {
            return isolationLevel != null
                ? new TransactionScope(TransactionScopeOption.Required,
                    new TransactionOptions
                    {
                        IsolationLevel = isolationLevel.Value,
                        Timeout = _options.TransactionTimeout
                    })
                : new TransactionScope();
        }

        internal void UseStatelessSession([InstantHandle] Action<IStatelessSession> action)
        {
            using (var session = GetStatelessSession())
            {
                action(session);
            }
        }

        internal T UseStatelessSession<T>([InstantHandle] Func<IStatelessSession, T> func)
        {
            using (var session = GetStatelessSession())
            {
                return func(session);
            }
        }

        internal void UseSession([InstantHandle] Action<ISession> action)
        {
            using (var session = GetStatefulSession())
            {
                action(session);
            }
        }

        internal T UseSession<T>([InstantHandle] Func<ISession, T> func)
        {
            using (var session = GetStatefulSession())
            {
                return func(session);
            }
        }

        internal void ReleaseSession(ISession session)
        {
            if (session != null)
            {
                session.Dispose();
            }
        }

        internal void ReleaseSession(IStatelessSession session)
        {
            session?.Dispose();
        }


        private ISessionFactory GetSessionFactory(IPersistenceConfigurer configurer)
        {
            //SINGLETON!
            if (_sessionFactories.ContainsKey(configurer) && _sessionFactories[configurer] != null)
            {
                return _sessionFactories[configurer];
            }

            var fluentConfiguration = Fluently.Configure().Mappings(i => i.FluentMappings.AddFromAssemblyOf<_Hash>());

            _sessionFactories[configurer] = fluentConfiguration
                .Database(configurer)
                .BuildSessionFactory();
            return _sessionFactories[configurer];
        }

        private IPersistenceConfigurer GetConfigurer()
        {
            if (_configurer == null)
            {
                _configurer = ConfigurerFunc();
            }
            return _configurer;
        }


        internal ISession GetStatefulSession()
        {
            DoTestBuild();
            return GetSessionFactory(GetConfigurer()).OpenSession();
        }

        private IStatelessSession GetStatelessSession()
        {
            DoTestBuild();
            return GetSessionFactory(GetConfigurer()).OpenStatelessSession();
        }

        private void DoTestBuild()
        {
            if (_options.PrepareSchemaIfNecessary)
            {
                if (!_testBuild)
                {
                    Logger.Info("Start installing Hangfire SQL objects...");
                    Fluently.Configure().Mappings(i => i.FluentMappings.AddFromAssemblyOf<_Hash>())
                        .Database(GetConfigurer())
                        .ExposeConfiguration(cfg =>
                        {
                            var a = new SchemaUpdate(cfg);
                            using (var stringWriter = new StringWriter())
                            {
                                string _last = null;
                                try
                                {
                                    a.Execute(i =>
                                    {
                                        _last = i;
                                        stringWriter.WriteLine(i);
                                    }, true);
                                }
                                catch (Exception ex)
                                {
                                    Logger.ErrorException(string.Format("Can't do schema update '{0}'", _last), ex);
                                    throw;
                                }
                            }
                        })
                        .BuildConfiguration();
                    _testBuild = true;
                    Logger.Info("Hangfire SQL objects installed.");
                }
            }
        }
    }
}