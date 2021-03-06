﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaFileManager.Desktop.Models;
using Microsoft.AppCenter.Crashes;
using Telerik.Windows.Controls;
using Analytics = Microsoft.AppCenter.Analytics.Analytics;

// ReSharper disable InconsistentNaming
namespace MediaFileManager.Desktop.Views
{
    public partial class VideoFilesView : UserControl, IDisposable
    {
        private readonly BackgroundWorker renumberWorker;
        private readonly RadOpenFolderDialog openFolderDialog;
        private readonly ObservableCollection<OutputMessage> StatusMessages = new ObservableCollection<OutputMessage>();

        public VideoFilesView()
        {
            InitializeComponent();

            Seasons = new ObservableCollection<string>();
            Episodes = new ObservableCollection<string>();
            RenamedEpisodesPreviewList = new ObservableCollection<string>();

            openFolderDialog = new RadOpenFolderDialog { Owner = this, ExpandToCurrentDirectory = false };

            SeasonsListBox.ItemsSource = Seasons;
            EpisodesListBox.ItemsSource = Episodes;
            EpisodeRenamedPreviewListBox.ItemsSource = RenamedEpisodesPreviewList;
            StatusListBox.ItemsSource = StatusMessages;

            renumberWorker = new BackgroundWorker { WorkerReportsProgress = true };
            renumberWorker.DoWork += RenumberWorker_DoWork;
            renumberWorker.ProgressChanged += RenumberWorker_ProgressChanged;
            renumberWorker.RunWorkerCompleted += RenumberWorker_RunWorkerCompleted;

            WriteOutput($"Ready, open a folder to begin.", OutputMessageLevel.Success);
        }

        public ObservableCollection<string> Seasons { get; }

        public ObservableCollection<string> Episodes { get; }

        public ObservableCollection<string> RenamedEpisodesPreviewList { get; }

        #region Source operations

        private void SelectFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                WriteOutput($"Opening folder picker...", OutputMessageLevel.Normal);

                busyIndicator.IsBusy = true;
                busyIndicator.BusyContent = "opening folder...";
                busyIndicator.IsIndeterminate = true;

                if (!string.IsNullOrEmpty(Properties.Settings.Default.LastFolder))
                {
                    // Need to bump up one level from the last folder location
                    var topDirectoryInfo = Directory.GetParent(Properties.Settings.Default.LastFolder);

                    openFolderDialog.InitialDirectory = topDirectoryInfo.FullName;

                    WriteOutput($"Starting at saved folder.", OutputMessageLevel.Normal);
                }
                else
                {
                    WriteOutput($"No saved folder, starting at root.", OutputMessageLevel.Warning);
                }

                openFolderDialog.ShowDialog();

                if (openFolderDialog.DialogResult != true)
                {
                    WriteOutput($"Canceled folder selection.", OutputMessageLevel.Normal);
                    return;
                }
                else
                {
                    Properties.Settings.Default.LastFolder = openFolderDialog.FileName;
                    Properties.Settings.Default.Save();
                }

                Reset();

                busyIndicator.BusyContent = $"searching for seasons...";

                var seasonsResult = Directory.EnumerateDirectories(openFolderDialog.FileName).ToList();

                Seasons.Clear();

                foreach (var season in seasonsResult)
                {
                    Seasons.Add(season);

                    busyIndicator.BusyContent = $"added {season}";
                }

                if (Seasons.Count == 0)
                {
                    WriteOutput("No seasons detected, make sure there are subfolders with season number.", OutputMessageLevel.Warning);
                }
                else if (Seasons.Count == 1)
                {
                    WriteOutput($"Opened '{System.IO.Path.GetFileName(openFolderDialog.FileName)}' ({Seasons.Count} season).", OutputMessageLevel.Success);
                }
                else
                {
                    WriteOutput($"Opened '{System.IO.Path.GetFileName(openFolderDialog.FileName)}' ({Seasons.Count} seasons).", OutputMessageLevel.Success);
                }

                Analytics.TrackEvent("Video Folder Opened", new Dictionary<string, string>
                {
                    { "Seasons", Seasons.Count.ToString(CultureInfo.InvariantCulture) }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

                WriteOutput(ex.Message, OutputMessageLevel.Error);

                Crashes.TrackError(ex, new Dictionary<string, string>
                {
                    { "Folder Open", "TV Show" }
                });
            }
            finally
            {
                busyIndicator.BusyContent = "";
                busyIndicator.IsBusy = false;
                busyIndicator.IsIndeterminate = false;
            }
        }

        private void SeasonsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            Episodes.Clear();

            foreach (string season in SeasonsListBox.SelectedItems)
            {
                var episodesResult = Directory.EnumerateFiles(season);

                foreach (var filePath in episodesResult)
                {
                    Episodes.Add(filePath);
                }
            }

            if (SeasonsListBox.SelectedItems.Count == 0)
            {
                WriteOutput("Selections cleared.", OutputMessageLevel.Warning);
            }
            else if (SeasonsListBox.SelectedItems.Count == 1)
            {
                WriteOutput($"{System.IO.Path.GetFileName(openFolderDialog.FileName)} selected ({Episodes.Count} episodes).", OutputMessageLevel.Informational);
            }
            else
            {
                WriteOutput($"{SeasonsListBox.SelectedItems.Count} seasons selected ({Episodes.Count} total episodes).", OutputMessageLevel.Informational);
            }

            Analytics.TrackEvent("Season Selection", new Dictionary<string, string>
            {
                { "Selected Seasons", SeasonsListBox.SelectedItems.Count.ToString(CultureInfo.InvariantCulture) }
            });
        }

        private void EpisodesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (e.AddedItems == null)
                return;

            if (EpisodesListBox.SelectedItems.Count <= 0)
                return;

            var firstEpisodeFilePath = e.AddedItems.OfType<string>().FirstOrDefault();

            string curName = System.IO.Path.GetFileName(firstEpisodeFilePath);

            if (!string.IsNullOrEmpty(curName))
            {
                EpisodeName_Renaming_TextBox.Text = curName;
                EpisodeName_Renumbering_TextBox.Text = curName;
            }
            else
            {
                EpisodeName_Renaming_TextBox.Text = string.Empty;
                EpisodeName_Renumbering_TextBox.Text = string.Empty;
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Reset();
            WriteOutput("Reset complete! Open a folder to continue.", OutputMessageLevel.Success);

            Analytics.TrackEvent("User Reset", new Dictionary<string, string>
            {
                { "View Type" , "Videos" }
            });
        }

        #endregion

        #region Renaming operation

        private void EpisodeNameTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            OriginalTextBox_Renaming.Text = EpisodeName_Renaming_TextBox.SelectedText;
        }

        private void OriginalTextBox_Renaming_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            UpdateFileNameButton.IsEnabled = !string.IsNullOrEmpty(OriginalTextBox_Renaming?.Text);
        }

        private async void UpdateFileNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(OriginalTextBox_Renaming.Text))
            {
                WriteOutput("No text selected, aborting file name update.", OutputMessageLevel.Error);
                return;
            }
            else
            {
                WriteOutput("Renaming operation starting...", OutputMessageLevel.Warning);
            }

            // variables for background thread access
            var selectedItems = EpisodesListBox.SelectedItems.Cast<string>().ToList();
            var selectedText = OriginalTextBox_Renaming.Text;
            var replacementText = ReplacementTextBox.Text;

            busyIndicator.IsBusy = true;
            busyIndicator.BusyContent = "updating file names...";
            busyIndicator.IsIndeterminate = false;
            busyIndicator.ProgressValue = 0;

            await Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < selectedItems.Count; i++)
                    {
                        var episodeFilePath = selectedItems[i];

                        // Need to separate name and path in order to prefix the file name
                        string curDir = Path.GetDirectoryName(episodeFilePath);

                        if (string.IsNullOrEmpty(curDir))
                        {
                            WriteOutput($"Could not find directory, skipping.", OutputMessageLevel.Error);
                            continue;
                        }

                        string curName = Path.GetFileName(episodeFilePath);

                        if (string.IsNullOrEmpty(curName))
                        {
                            WriteOutput($"Could not find file, skipping.", OutputMessageLevel.Error);
                            continue;
                        }

                        // Replace the selected text with the new text (support empty replacement to remove text)
                        string newName = curName.Replace(selectedText, replacementText, StringComparison.InvariantCulture);

                        // Rename the file
                        File.Move(episodeFilePath, Path.Combine(curDir, newName));

                        // Need to dispatch back to UI thread, variables to avoid access to modified closure problem
                        var progressComplete = i / selectedItems.Count * 100;
                        var progressText = $"Renaming - '{selectedText}' to '{replacementText}'...";

                        Dispatcher.Invoke(() =>
                        {
                            busyIndicator.ProgressValue = progressComplete;
                            busyIndicator.BusyContent = $"Completed {progressText}...";
                        });
                    }

                    WriteOutput($"Renaming operation complete!", OutputMessageLevel.Success);
                }
                catch (Exception ex)
                {
                    WriteOutput(ex.Message, OutputMessageLevel.Error);

                    Crashes.TrackError(ex, new Dictionary<string, string>
                    {
                        { "Rename Episode", "TV Show" }
                    });
                }
            }).ConfigureAwait(true);

            RefreshEpisodesList();

            Analytics.TrackEvent("Episode Renaming Complete", new Dictionary<string, string>
            {
                { "Total Episodes", Episodes.Count.ToString(CultureInfo.InvariantCulture) },
                { "Episodes Renamed", EpisodesListBox.SelectedItems.Count.ToString(CultureInfo.InvariantCulture) }
            });

            busyIndicator.BusyContent = "";
            busyIndicator.IsBusy = false;
            busyIndicator.ProgressValue = 0;
        }

        #endregion

        #region Renumbering operation

        private void EpisodeNameTextBox_Renumbering_SelectionChanged(object sender, RoutedEventArgs e)
        {
            OriginalTextBox_Renumbering.Text = EpisodeName_Renumbering_TextBox.SelectedText;
        }

        private void OriginalTextBox_Renumbering_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(OriginalTextBox_Renumbering?.Text) || string.IsNullOrEmpty(SeasonNumberTextBox?.Text))
            {
                RenumberingButton.IsEnabled = false;
            }
            else
            {
                RenumberingButton.IsEnabled = true;
            }
        }

        private void SeasonNumberTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // We also want to make sure user entered a season number
            if (string.IsNullOrEmpty(OriginalTextBox_Renumbering?.Text) || string.IsNullOrEmpty(SeasonNumberTextBox?.Text))
            {
                RenumberingButton.IsEnabled = false;
            }
            else
            {
                RenumberingButton.IsEnabled = true;
            }
        }

        private void RenumberingButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(OriginalTextBox_Renumbering?.Text))
            {
                WriteOutput("You must selected text that will be replaced by the season and episode number.", OutputMessageLevel.Error);
                return;
            }

            if (string.IsNullOrEmpty(SeasonNumberTextBox?.Text) || !int.TryParse(SeasonNumberTextBox?.Text, out int seasonNumber))
            {
                WriteOutput("You must enter a valid two-digit number for the season.", OutputMessageLevel.Error);
                return;
            }

            if (string.IsNullOrEmpty(EpisodeStartTextBox?.Text) || string.IsNullOrEmpty(EpisodeEndTextBox?.Text))
            {
                WriteOutput("You must enter a first and last episode number.", OutputMessageLevel.Error);
                return;
            }

            if (!int.TryParse(EpisodeStartTextBox?.Text, out int startingEpisodeNumber) || !int.TryParse(EpisodeEndTextBox?.Text, out int lastEpisodeNumber))
            {
                WriteOutput("You must use a valid two-digit value for the start and end episode number.", OutputMessageLevel.Error);
                return;
            }

            // Make sure the user has entered the correct number of episodes
            if (lastEpisodeNumber - startingEpisodeNumber + 1 != EpisodesListBox.SelectedItems.Count)
            {
                WriteOutput("The episode numbers do not match the total selected episodes, you need to have the same number of episode number as selected episodes.", OutputMessageLevel.Error);
                return;
            }

            busyIndicator.IsBusy = true;
            busyIndicator.BusyContent = "re-numbering and renaming files...";
            busyIndicator.IsIndeterminate = false;
            busyIndicator.ProgressValue = 0;

            if (RenamedEpisodesPreviewList.Any())
            {
                RenamedEpisodesPreviewList.Clear();
            }

            renumberWorker.RunWorkerAsync(new WorkerParameters
            {
                IsPreview = true,
                SelectedEpisodes = EpisodesListBox.SelectedItems.Cast<string>().ToList(),
                SeasonNumber = seasonNumber,
                EpisodeNumberStart = startingEpisodeNumber,
                EpisodeNumberEnd = lastEpisodeNumber,
                SelectedTextLength = OriginalTextBox_Renumbering.Text.Length
            });
        }

        private void RenumberWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            if(!(e.Argument is WorkerParameters workerParameter))
                return;

            try
            {
                int currentEpisodeNumber = workerParameter.EpisodeNumberStart;

                for (int i = 0; i < workerParameter.SelectedEpisodes.Count; i++)
                {
                    var episodeFilePath = workerParameter.SelectedEpisodes[i];

                    string curDir = System.IO.Path.GetDirectoryName(episodeFilePath);

                    if (string.IsNullOrEmpty(curDir))
                    {
                        WriteOutput("Could not find directory, skipping.", OutputMessageLevel.Error);
                        continue;
                    }

                    string curName = System.IO.Path.GetFileName(episodeFilePath);

                    if (string.IsNullOrEmpty(curName))
                    {
                        WriteOutput("Could not find file, skipping.", OutputMessageLevel.Error);
                        continue;
                    }

                    // Using substring and Length so that user doesn't need an exact natch (e.g. episode number will be different for each selected episode, thus only one selection will get renamed... the exact match)
                    var selectedText = curName.Substring(0, workerParameter.SelectedTextLength);

                    // Get the show name to workaround not being able to select exact text
                    var showName = Directory.GetParent(episodeFilePath)?.Parent?.Name;

                    // Prefix the name name with the Show, then the season, then the episode number
                    string newName = curName.Replace(selectedText, $"{showName} - S{workerParameter.SeasonNumber}E{currentEpisodeNumber:00} -", StringComparison.InvariantCulture);

                    // If this is not a preview run, invoke the file rename
                    if (!workerParameter.IsPreview)
                    {
                        File.Move(episodeFilePath, System.IO.Path.Combine(curDir, newName));
                    }

                    // Increment the episode number
                    currentEpisodeNumber++;

                    // Report progress
                    var progressParameter = new WorkerProgress
                    {
                        IsPreview = workerParameter.IsPreview,
                        PercentComplete = i / workerParameter.SelectedEpisodes.Count * 100,
                        BusyMessage = $"Completed: S{workerParameter.SeasonNumber}E{currentEpisodeNumber:00}...",
                        FileName = newName
                    };

                    renumberWorker.ReportProgress(progressParameter.PercentComplete, progressParameter);
                }

                e.Result = new WorkerResult()
                {
                    FinalMessage = $"Complete, renumbered {workerParameter.SelectedEpisodes.Count} episodes.",
                    IsPreview = workerParameter.IsPreview
                };
            }
            catch (Exception ex)
            {
                WriteOutput(ex.Message, OutputMessageLevel.Error);

                Crashes.TrackError(ex, new Dictionary<string, string>
                {
                    { "Renumber Episode", "TV Show" }
                });
            }
        }

        private void RenumberWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            if (e.UserState is WorkerProgress resultParam)
            {
                busyIndicator.ProgressValue = e.ProgressPercentage;
                busyIndicator.BusyContent = resultParam.BusyMessage;

                // If this is a preview run, populate the preview ListBox
                if (resultParam.IsPreview)
                {
                    RenamedEpisodesPreviewList.Add(resultParam.FileName);
                }
            }
        }

        private void RenumberWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Result is WorkerResult resultParameter)
            {
                WriteOutput(resultParameter.FinalMessage, OutputMessageLevel.Success);

                Analytics.TrackEvent("Episode Renumbering Complete", new Dictionary<string, string>
                {
                    { "Total Episodes", Episodes.Count.ToString(CultureInfo.InvariantCulture) },
                    { "Episodes Renumbered", EpisodesListBox.SelectedItems.Count.ToString(CultureInfo.InvariantCulture) }
                });

                if (!resultParameter.IsPreview)
                {
                    RenamedEpisodesPreviewList.Clear();

                    RefreshEpisodesList();

                    EpisodesPane.IsSelected = true;

                    ApproveResultButton.IsEnabled = false;
                    CancelResultButton.IsEnabled = false;
                }
                else
                {
                    ResultPreviewPane.IsSelected = true;

                    ApproveResultButton.IsEnabled = true;
                    CancelResultButton.IsEnabled = true;
                }
            }

            busyIndicator.BusyContent = "";
            busyIndicator.IsBusy = false;
            busyIndicator.ProgressValue = 0;
        }

        private void ApproveResultButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(OriginalTextBox_Renumbering?.Text))
            {
                WriteOutput($"You must selected text that will be replaced by the season and episode number.", OutputMessageLevel.Error);
                return;
            }

            if (string.IsNullOrEmpty(SeasonNumberTextBox?.Text) || !int.TryParse(SeasonNumberTextBox?.Text, out int seasonNumber))
            {
                WriteOutput($"You must enter a valid two-digit number for the season.", OutputMessageLevel.Error);
                return;
            }

            if (string.IsNullOrEmpty(EpisodeStartTextBox?.Text) || string.IsNullOrEmpty(EpisodeEndTextBox?.Text))
            {
                WriteOutput($"You must enter a first and last episode number.", OutputMessageLevel.Error);
                return;
            }

            if (!int.TryParse(EpisodeStartTextBox?.Text, out int startingEpisodeNumber) || !int.TryParse(EpisodeEndTextBox?.Text, out int lastEpisodeNumber))
            {
                WriteOutput($"You must use a valid two-digit value for the start and end episode number.", OutputMessageLevel.Error);
                return;
            }

            // Make sure the user has entered the correct number of episodes
            if (lastEpisodeNumber - startingEpisodeNumber + 1 != EpisodesListBox.SelectedItems.Count)
            {
                WriteOutput($"The episode numbers do not match the total selected episodes, you need to have the same number of episode number as selected episodes.", OutputMessageLevel.Error);
                return;
            }

            Analytics.TrackEvent("Renumbering Preview Approved");

            busyIndicator.IsBusy = true;
            busyIndicator.BusyContent = "re-numbering and renaming files...";
            busyIndicator.IsIndeterminate = false;
            busyIndicator.ProgressValue = 0;

            renumberWorker.RunWorkerAsync(new WorkerParameters
            {
                SelectedEpisodes = EpisodesListBox.SelectedItems.Cast<string>().ToList(),
                SeasonNumber = seasonNumber,
                EpisodeNumberStart = startingEpisodeNumber,
                EpisodeNumberEnd = lastEpisodeNumber,
                SelectedTextLength = OriginalTextBox_Renumbering.Text.Length
            });
        }

        private void CancelResultButton_Click(object sender, RoutedEventArgs e)
        {
            RenamedEpisodesPreviewList.Clear();

            EpisodesPane.IsSelected = true;

            ApproveResultButton.IsEnabled = false;
            CancelResultButton.IsEnabled = false;

            Analytics.TrackEvent("Cancel Results Preview");
        }

        #endregion

        private void RefreshEpisodesList()
        {
            Episodes.Clear();

            foreach (string season in SeasonsListBox.SelectedItems)
            {
                var folderName = System.IO.Path.GetFileName(season);

                if (string.IsNullOrEmpty(folderName))
                {
                    WriteOutput($"Could not identify directory.", OutputMessageLevel.Error);
                    return;
                }

                var episodesResult = Directory.EnumerateFiles(season);

                WriteOutput($"Searching {folderName} episodes...", OutputMessageLevel.Normal);

                foreach (var filePath in episodesResult)
                {
                    if (System.IO.Path.HasExtension(filePath))
                    {
                        Episodes.Add(filePath);

                        WriteOutput($"Adding {filePath}...", OutputMessageLevel.Normal, true);
                    }
                }

                WriteOutput($"Refreshed {folderName} {Episodes.Count} episodes.", OutputMessageLevel.Normal, true);
            }
        }

        private void Reset()
        {
            EpisodeName_Renaming_TextBox.Text = string.Empty;
            EpisodeName_Renumbering_TextBox.Text = string.Empty;

            OriginalTextBox_Renaming.Text = string.Empty;
            OriginalTextBox_Renumbering.Text = string.Empty;

            ReplacementTextBox.Text = string.Empty;
            SeasonNumberTextBox.Text = string.Empty;

            Seasons.Clear();
            Episodes.Clear();
            StatusMessages.Clear();
        }

        private void WriteOutput(string text, OutputMessageLevel level, bool removeLastItem = false)
        {
            var messageColor = Colors.Gray;

            switch (level)
            {
                case OutputMessageLevel.Normal:
                    messageColor = Colors.Black;
                    break;
                case OutputMessageLevel.Informational:
                    messageColor = Colors.Gray;
                    break;
                case OutputMessageLevel.Success:
                    messageColor = Colors.Green;
                    break;
                case OutputMessageLevel.Warning:
                    messageColor = Colors.Goldenrod;
                    break;
                case OutputMessageLevel.Error:
                    messageColor = Colors.Red;
                    break;
            }

            if (this.Dispatcher.CheckAccess())
            {
                if (removeLastItem && StatusMessages.Count > 0)
                {
                    StatusMessages.Remove(StatusMessages.LastOrDefault());
                }

                var message = new OutputMessage
                {
                    Message = text,
                    MessageColor = messageColor
                };

                StatusMessages.Add(message);
                StatusListBox.ScrollIntoView(message);
            }
            else
            {
                this.Dispatcher.Invoke(() =>
                {
                    if (removeLastItem && StatusMessages.Count > 0)
                    {
                        StatusMessages.Remove(StatusMessages.LastOrDefault());
                    }

                    var message = new OutputMessage
                    {
                        Message = text,
                        MessageColor = messageColor
                    };

                    StatusMessages.Add(message);
                    StatusListBox.ScrollIntoView(message);
                });
            }
        }

        public void Dispose()
        {
            renumberWorker.Dispose();
        }
    }
}
