﻿//-----------------------------------------------------------------------
// <copyright file="EtwTelemetryModule.cs" company="Microsoft">
//     Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.ApplicationInsights.EtwCollector
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights.EtwCollector.Implemenetation;
    using Microsoft.ApplicationInsights.EventSource.Shared.Implementation;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Implementation;
    using Microsoft.Diagnostics.Tracing;
    using Microsoft.Diagnostics.Tracing.Session;

    /// <summary>
    /// A module to trace data submitted via .NET framework <seealso cref="Microsoft.Diagnostics.Tracing.Session" /> class.
    /// </summary>
    public class EtwTelemetryModule : ITelemetryModule, IDisposable
    {
        private TelemetryClient client;
        private bool isDisposed = false;
        private bool isInitialized = false;
        private ITraceEventSession traceEventSession;
        private List<Guid> enabledProviderIds;
        private List<string> enabledProviderNames;
        private readonly object lockObject;

        /// <summary>
        /// Gets the list of ETW Provider listening requests (information about which providers should be traced).
        /// </summary>
        public IList<EtwListeningRequest> Sources { get; private set; }

        /// <summary>
        /// EtwTelemetryModule default constructor
        /// </summary>
        public EtwTelemetryModule() : this(
            new AITraceEventSession(new TraceEventSession(string.Format(CultureInfo.InvariantCulture, "ApplicationInsights-{0}-{1}", nameof(EtwTelemetryModule), Guid.NewGuid()))),
            new Action<ITraceEventSession, TelemetryClient>((traceSession, client) =>
            {
                if (traceSession != null && traceSession.Source != null)
                {
                    traceSession.Source.Dynamic.All += traceEvent =>
                    {
                        traceEvent.Track(client);
                    };
                    traceSession.Source.Process();
                }
            }))
        {
        }

        internal EtwTelemetryModule(ITraceEventSession traceEventSession,
            Action<ITraceEventSession, TelemetryClient> startTraceEventSessionAction)
        {
            this.lockObject = new object();
            this.Sources = new List<EtwListeningRequest>();
            this.enabledProviderIds = new List<Guid>();
            this.enabledProviderNames = new List<string>();

            this.traceEventSession = traceEventSession;
            this.StartTraceEventSession = startTraceEventSessionAction;
        }

        private Action<ITraceEventSession, TelemetryClient> StartTraceEventSession
        {
            get;
            set;
        }

        /// <summary>
        /// Initializes the telemetry module and starts tracing ETW events specified via <see cref="Sources"/> property.
        /// </summary>
        /// <param name="configuration">Module configuration.</param>
        public void Initialize(TelemetryConfiguration configuration)
        {
            if (configuration == null)
            {
                EventSourceListenerEventSource.Log.ModuleInitializationFailed(
                    nameof(EtwTelemetryModule),
                    string.Format(CultureInfo.InvariantCulture, "Argument {0} is required. The initialization is terminated.", nameof(configuration)));
                return;
            }

            if (this.isDisposed)
            {
                EventSourceListenerEventSource.Log.ModuleInitializationFailed(nameof(EtwTelemetryModule),
                    "Can't initialize a module that is disposed. The initialization is terminated.");
                return;
            }

            bool? isProcessElevated = this.traceEventSession.IsElevated();
            if (!isProcessElevated.HasValue || !isProcessElevated.Value)
            {
                EventSourceListenerEventSource.Log.ModuleInitializationFailed(nameof(EtwTelemetryModule),
                    "The process is required to be elevated to enable ETW providers. The initialization is terminated.");
                return;
            }

            lock (this.lockObject)
            {
                this.client = new TelemetryClient(configuration);

                // sdkVersionIdentifier will be used in telemtry entry as a identifier for the sender.
                // The value will look like: etw:x.x.x-x
                const string sdkVersionIdentifier = "etw:";
                this.client.Context.GetInternalContext().SdkVersion = SdkVersionUtils.GetSdkVersion(sdkVersionIdentifier);


                if (this.isInitialized)
                {
                    this.isInitialized = false;
                    DisableProviders();
                    this.enabledProviderIds.Clear();
                    this.enabledProviderNames.Clear();
                }

                if (this.Sources.Count == 0)
                {
                    EventSourceListenerEventSource.Log.NoSourcesConfigured(moduleName: nameof(EtwTelemetryModule));
                    return;
                }

                EnableProviders();
                try
                {
                    // Start the trace session
                    Task.Factory.StartNew(() => this.StartTraceEventSession(this.traceEventSession, this.client), TaskCreationOptions.LongRunning);
                }
                finally
                {
                    this.isInitialized = true;
                }
            }
        }

        private void EnableProviders()
        {
            foreach (EtwListeningRequest request in this.Sources)
            {
                EnableProvider(request);
            }
        }

        private void EnableProvider(EtwListeningRequest request)
        {
            try
            {
                request.Validate();
            }
            catch (Exception ex)
            {
                EventSourceListenerEventSource.Log.FailedToEnableProviders(nameof(EtwTelemetryModule),
                    string.IsNullOrEmpty(request.ProviderName) ? request.ProviderGuid.ToString() : request.ProviderName,
                    ex.Message);
            }

            try
            {
                if (request.ProviderGuid != Guid.Empty)
                {
                    EnableProvider(request.ProviderGuid, request.Level, request.Keywords);
                }
                else
                {
                    EnableProvider(request.ProviderName, request.Level, request.Keywords);
                }
            }
            catch (Exception ex)
            {
                EventSourceListenerEventSource.Log.FailedToEnableProviders(nameof(EtwTelemetryModule),
                    string.IsNullOrEmpty(request.ProviderName) ? request.ProviderGuid.ToString() : request.ProviderName,
                    ex.Message);
            }
        }

        private void EnableProvider(Guid providerGuid, TraceEventLevel level, ulong keywords)
        {
            this.traceEventSession.EnableProvider(providerGuid, level, keywords);
            enabledProviderIds.Add(providerGuid);
        }

        private void EnableProvider(string providerName, TraceEventLevel level, ulong keywords)
        {
            this.traceEventSession.EnableProvider(providerName, level, keywords);
            enabledProviderNames.Add(providerName);
        }

        private void DisableProviders()
        {
            foreach (Guid id in enabledProviderIds)
            {
                this.traceEventSession.DisableProvider(id);
            }
            foreach (string providerName in enabledProviderNames)
            {
                this.traceEventSession.DisableProvider(providerName);
            }
        }

        /// <summary>
        /// Disposes the module.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the module
        /// </summary>
        /// <param name="isDisposing"></param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (this.isDisposed) return;

            // Mark this object as disposed even when disposing run into exception, which is not expected.
            this.isDisposed = true;
            if (isDisposing)
            {
                if (traceEventSession != null)
                {
                    traceEventSession.Dispose();
                }
            }
        }
    }
}
