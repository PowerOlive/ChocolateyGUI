﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright company="Chocolatey" file="LocalSourceViewModel.cs">
//   Copyright 2014 - Present Rob Reynolds, the maintainers of Chocolatey, and RealDimensions Software, LLC
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Xml;
using Caliburn.Micro;
using ChocolateyGui.Models;
using ChocolateyGui.Models.Messages;
using ChocolateyGui.Services;
using ChocolateyGui.Utilities.Extensions;
using ChocolateyGui.ViewModels.Items;
using Serilog;

namespace ChocolateyGui.ViewModels
{
    public class LocalSourceViewModel : Screen, ISourceViewModelBase, IHandleWithTask<PackageChangedMessage>
    {
        private static readonly ILogger Logger = Log.ForContext<LocalSourceViewModel>();
        private readonly IChocolateyPackageService _chocolateyService;
        private readonly List<IPackageViewModel> _packages;
        private readonly IPersistenceService _persistenceService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IProgressService _progressService;
        private bool _exportAll = true;
        private bool _hasLoaded;
        private bool _matchWord;
        private ObservableCollection<IPackageViewModel> _packageViewModels;
        private string _searchQuery;
        private bool _showOnlyPackagesWithUpdate;
        private string _sortColumn;
        private bool _sortDescending;

        public LocalSourceViewModel(
            IChocolateyPackageService chocolateyService,
            IProgressService progressService,
            IPersistenceService persistenceService,
            IEventAggregator eventAggregator,
            string name)
        {
            _chocolateyService = chocolateyService;
            _progressService = progressService;
            _persistenceService = persistenceService;
            _eventAggregator = eventAggregator;

            Name = name;
            Packages = new ObservableCollection<IPackageViewModel>();
            _packages = new List<IPackageViewModel>();
            eventAggregator.Subscribe(this);
        }

        public bool ShowOnlyPackagesWithUpdate
        {
            get { return _showOnlyPackagesWithUpdate; }
            set { this.SetPropertyValue(ref _showOnlyPackagesWithUpdate, value); }
        }

        public bool MatchWord
        {
            get { return _matchWord; }
            set { this.SetPropertyValue(ref _matchWord, value); }
        }

        public ObservableCollection<IPackageViewModel> Packages
        {
            get { return _packageViewModels; }
            set { this.SetPropertyValue(ref _packageViewModels, value); }
        }

        public string SearchQuery
        {
            get { return _searchQuery; }
            set { this.SetPropertyValue(ref _searchQuery, value); }
        }

        public string SortColumn
        {
            get { return _sortColumn; }
            set { this.SetPropertyValue(ref _sortColumn, value); }
        }

        public bool SortDescending
        {
            get { return _sortDescending; }
            set { this.SetPropertyValue(ref _sortDescending, value); }
        }

        public bool CanUpdateAll()
        {
            return Packages.Any(p => p.CanUpdate);
        }

        public async void UpdateAll()
        {
            try
            {
                await _progressService.StartLoading("Packages", true);
                var token = _progressService.GetCancellationToken();
                var packages = Packages.Where(p => p.CanUpdate).ToList();
                double current = 0.0f;
                foreach (var package in packages)
                {
                    if (token.IsCancellationRequested)
                    {
                        await _progressService.StopLoading();
                        return;
                    }

                    _progressService.Report(Math.Min(current++ / packages.Count, 100));
                    await package.Update();
                }

                await _progressService.StopLoading();
                ShowOnlyPackagesWithUpdate = false;
                RefreshPackages();
            }
            catch (Exception ex)
            {
                Logger.Fatal("Updated all has failed.", ex);
                throw;
            }
        }

        public void ExportAll()
        {
            _exportAll = false;

            try
            {
                var fileStream = _persistenceService.SaveFile("*.config", "Config Files (.config)|*.config");

                if (fileStream == null)
                {
                    return;
                }

                var settings = new XmlWriterSettings {Indent = true};

                using (var xw = XmlWriter.Create(fileStream, settings))
                {
                    xw.WriteStartDocument();
                    xw.WriteStartElement("packages");

                    foreach (var package in Packages)
                    {
                        xw.WriteStartElement("package");
                        xw.WriteAttributeString("id", package.Id);
                        xw.WriteAttributeString("version", package.Version.ToString());
                        xw.WriteEndElement();
                    }

                    xw.WriteEndElement();
                }
            }
            catch (Exception ex)
            {
                Logger.Fatal("Export all has failed.", ex);
                throw;
            }
            finally
            {
                _exportAll = true;
            }
        }

        public bool ReceiveWeakEvent(Type managerType, object sender, EventArgs e)
        {
            if (sender is IChocolateyPackageService && e is PackagesChangedEventArgs)
            {
                LoadPackages().ConfigureAwait(false);
            }

            return true;
        }

        public bool CanExportAll()
        {
            return _exportAll;
        }

        public bool CanRefreshPackages()
        {
            return _hasLoaded;
        }

        public async void RefreshPackages()
        {
            await LoadPackages();
        }

        public async Task Handle(PackageChangedMessage message)
        {
            await LoadPackages();
        }

#pragma warning disable RECS0165 // Asynchronous methods should return a Task instead of void
        protected override async void OnInitialize()
#pragma warning restore RECS0165 // Asynchronous methods should return a Task instead of void
        {
            try
            {
                if (_hasLoaded)
                {
                    return;
                }

                await LoadPackages();

                Observable.FromEventPattern<PropertyChangedEventArgs>(this, "PropertyChanged")
                    .Where(
                        eventPattern =>
                            eventPattern.EventArgs.PropertyName == "MatchWord" ||
                            eventPattern.EventArgs.PropertyName == "SearchQuery" ||
                            eventPattern.EventArgs.PropertyName == "ShowOnlyPackagesWithUpdate")
                    .ObserveOnDispatcher()
                    .Subscribe(eventPattern => FilterPackages());

                _hasLoaded = true;

                var chocoPackage = _packages.FirstOrDefault(p => p.Id.ToLower() == "chocolatey");
                if (chocoPackage != null && chocoPackage.CanUpdate)
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    _progressService.ShowMessageAsync("Chocolatey", "There's an update available for chocolatey.")
                        .ConfigureAwait(false);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                }
            }
            catch (Exception ex)
            {
                Logger.Fatal("Local source control view model failed to load.", ex);
                throw;
            }
        }

        private void FilterPackages()
        {
            Packages.Clear();
            var query = _packages.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                query = MatchWord
                    ? query.Where(
                        package =>
                            string.Compare(
                                package.Title ?? package.Id,
                                SearchQuery,
                                StringComparison.OrdinalIgnoreCase) == 0)
                    : query.Where(
                        package =>
                            CultureInfo.CurrentCulture.CompareInfo.IndexOf(
                                package.Title ?? package.Id,
                                SearchQuery,
                                CompareOptions.OrdinalIgnoreCase) >= 0);
            }

            if (ShowOnlyPackagesWithUpdate)
            {
                query = query.Where(p => p.CanUpdate);
            }

            Packages = new ObservableCollection<IPackageViewModel>(query);
        }

        private async Task LoadPackages()
        {
            try
            {
                _packages.Clear();
                Packages.Clear();

                var packages = await _chocolateyService.GetInstalledPackages();
                foreach (var packageViewModel in packages)
                {
                    _packages.Add(packageViewModel);

                    if (packageViewModel.LatestVersion == null)
                    {
#pragma warning disable CS4014 // We want this to execute asynchrnously.
                        packageViewModel.RetrieveLatestVersion().ConfigureAwait(false);
#pragma warning restore CS4014
                    }
                }

                FilterPackages();
            }
            catch (Exception ex)
            {
                Logger.Fatal("Packages failed to load", ex);
                throw;
            }
        }

        public string Name { get; }
    }
}