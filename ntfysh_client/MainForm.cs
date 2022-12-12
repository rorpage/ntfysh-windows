﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json;
using ntfysh_client.Notifications;

namespace ntfysh_client
{
    public partial class MainForm : Form
    {
        private readonly NotificationListener _notificationListener;
        private bool _startInTray;
        private bool _trueExit;

        public MainForm(NotificationListener notificationListener, bool startInTray = false)
        {
            _notificationListener = notificationListener;
            _startInTray = startInTray;
            _notificationListener.OnNotificationReceive += OnNotificationReceive;
            _notificationListener.OnConnectionMultiAttemptFailure += OnConnectionMultiAttemptFailure;
            _notificationListener.OnConnectionCredentialsFailure += OnConnectionCredentialsFailure;
            
            InitializeComponent();
        }
        
        private void MainForm_Load(object sender, EventArgs e)
        {
            LoadSettings();
            LoadTopics();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (_startInTray)
            {
                _startInTray = false;
                
                /*
                 * TODO This little workaround prevents the window from appearing with a flash, but the taskbar icon appears for a moment.
                 *
                 * TODO This is because we must call SetVisibleCore(true) for the initial load events in the MainForm to fire, which is what triggers the listener
                 */
                Opacity = 0;
                base.SetVisibleCore(true);
                base.SetVisibleCore(false);
                Opacity = 1;
                
                return;
            }
            
            base.SetVisibleCore(value);
        }

        private void OnNotificationReceive(object sender, NotificationReceiveEventArgs e)
        {
            ToolTipIcon priorityIcon = e.Priority switch
            {
                NotificationPriority.Max => ToolTipIcon.Error,
                NotificationPriority.High => ToolTipIcon.Warning,
                NotificationPriority.Default => ToolTipIcon.Info,
                NotificationPriority.Low => ToolTipIcon.Info,
                NotificationPriority.Min => ToolTipIcon.None,
                _ => throw new ArgumentOutOfRangeException("Unknown priority received")
            };

            string finalTitle = string.IsNullOrWhiteSpace(e.Title) ? $"{e.Sender.TopicId}@{e.Sender.ServerUrl}" : e.Title;
            
            notifyIcon.ShowBalloonTip((int)TimeSpan.FromSeconds((double)Program.Settings.Timeout).TotalMilliseconds, finalTitle, e.Message, priorityIcon);
        }

        private void OnConnectionMultiAttemptFailure(NotificationListener sender, SubscribedTopic topic)
        {
            MessageBox.Show($"Connecting to topic ID '{topic.TopicId}' on server '{topic.ServerUrl}' failed after multiple attempts.\n\nThis topic ID will be ignored and you will not receive notifications for it until you restart the application.", "Connection Failure", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        
        private void OnConnectionCredentialsFailure(NotificationListener sender, SubscribedTopic topic)
        {
            string reason = string.IsNullOrWhiteSpace(topic.Username) ? "credentials are required but were not provided" : "the entered credentials are incorrect";
            
            MessageBox.Show($"Connecting to topic ID '{topic.TopicId}' on server '{topic.ServerUrl}' failed because {reason}.\n\nThis topic ID will be ignored and you will not receive notifications for it until you correct the credentials.", "Connection Authentication Failure", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void subscribeNewTopic_Click(object sender, EventArgs e)
        {
            using SubscribeDialog dialog = new SubscribeDialog(notificationTopics);
            DialogResult result = dialog.ShowDialog();

            //Do not subscribe on cancelled dialog
            if (result != DialogResult.OK) return;
                
            //Subscribe
            if (dialog.UseWebsockets)
            {
                _notificationListener.SubscribeToTopicUsingWebsocket(dialog.Unique, dialog.TopicId, dialog.ServerUrl, dialog.Username, dialog.Password);
            }
            else
            {
                _notificationListener.SubscribeToTopicUsingLongHttpJson(dialog.Unique, dialog.TopicId, dialog.ServerUrl, dialog.Username, dialog.Password);
            }

            //Add to the user visible list
            notificationTopics.Items.Add(dialog.Unique);
                    
            //Save the topics persistently
            SaveTopicsToFile();
        }

        private async void removeSelectedTopics_Click(object sender, EventArgs e)
        {
            while (notificationTopics.SelectedIndex > -1)
            {
                string topicUniqueString = (string)notificationTopics.Items[notificationTopics.SelectedIndex];
                
                await _notificationListener.UnsubscribeFromTopicAsync(topicUniqueString);
                notificationTopics.Items.Remove(topicUniqueString);
            }

            SaveTopicsToFile();
        }
        
        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using SettingsDialog dialog = new();

            //Load current settings into dialog
            dialog.Timeout = Program.Settings.Timeout;
            
            //Show dialog
            DialogResult result = dialog.ShowDialog();

            //Do not save on cancelled dialog
            if (result != DialogResult.OK) return;

            //Read new settings from dialog
            Program.Settings.Timeout = dialog.Timeout;
            
            //Save new settings persistently
            SaveSettingsToFile();
        }

        private void notificationTopics_SelectedValueChanged(object sender, EventArgs e)
        {
            removeSelectedTopics.Enabled = notificationTopics.SelectedIndices.Count > 0;
        }

        private void notificationTopics_Click(object sender, EventArgs e)
        {
            MouseEventArgs ev = (MouseEventArgs)e;
            int clickedItemIndex = notificationTopics.IndexFromPoint(new Point(ev.X, ev.Y));

            if (clickedItemIndex == -1) notificationTopics.ClearSelected();
        }

        private void notifyIcon_Click(object sender, EventArgs e)
        {
            MouseEventArgs mouseEv = (MouseEventArgs)e;
            
            if (mouseEv.Button != MouseButtons.Left) return;
            
            Visible = !Visible;
            BringToFront();
        }

        private void showControlWindowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Visible = true;
            BringToFront();
        }

        private string GetTopicsFilePath()
        {
            string binaryDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Unable to determine path for application");
            return Path.Combine(binaryDirectory ?? throw new InvalidOperationException("Unable to determine path for topics file"), "topics.json");
        }
        
        private string GetLegacyTopicsFilePath()
        {
            string binaryDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Unable to determine path for application");
            return Path.Combine(binaryDirectory ?? throw new InvalidOperationException("Unable to determine path for legacy topics file"), "topics.txt");
        }

        private void SaveTopicsToFile()
        {
            string topicsSerialised = JsonConvert.SerializeObject(_notificationListener.SubscribedTopicsByUnique.Select(st => st.Value).ToList(), Formatting.Indented);
            
            File.WriteAllText(GetTopicsFilePath(), topicsSerialised);
        }
        
        private string GetSettingsFilePath()
        {
            string binaryDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Unable to determine path for application");
            return Path.Combine(binaryDirectory ?? throw new InvalidOperationException("Unable to determine path for settings file"), "settings.json");
        }
        
        private void SaveSettingsToFile()
        {
            string settingsSerialised = JsonConvert.SerializeObject(Program.Settings, Formatting.Indented);
            
            File.WriteAllText(GetSettingsFilePath(), settingsSerialised);
        }

        private void LoadTopics()
        {
            string legacyTopicsPath = GetLegacyTopicsFilePath();
            string topicsFilePath = GetTopicsFilePath();

            //If we have an old format topics file. Convert it to the new format!
            if (File.Exists(legacyTopicsPath))
            {
                //Read old format
                List<string> legacyTopics = new List<string>();
                
                using (StreamReader reader = new StreamReader(legacyTopicsPath))
                {
                    while (!reader.EndOfStream)
                    {
                        string? legacyTopic = reader.ReadLine();
                        if (!string.IsNullOrWhiteSpace(legacyTopic)) legacyTopics.Add(legacyTopic);
                    }
                }

                //Assemble new format
                List<SubscribedTopic> newTopics = legacyTopics.Select(lt => new SubscribedTopic(lt, "https://ntfy.sh", null, null)).ToList();

                string newFormatSerialised = JsonConvert.SerializeObject(newTopics, Formatting.Indented);
                
                //Write new format
                File.WriteAllText(topicsFilePath, newFormatSerialised);
                
                //Delete old format
                File.Delete(legacyTopicsPath);
            }
            
            //Check if we have any topics file on disk to load
            if (!File.Exists(topicsFilePath)) return;
            
            //We have a topics file. Load it!
            string topicsSerialised = File.ReadAllText(topicsFilePath);

            //Check if the file is empty
            if (string.IsNullOrWhiteSpace(topicsSerialised))
            {
                //The file is empty. May as well remove it and consider it nonexistent
                File.Delete(topicsFilePath);
                return;
            }

            //Deserialise the topics
            List<SubscribedTopic>? topics = JsonConvert.DeserializeObject<List<SubscribedTopic>>(topicsSerialised);

            if (topics is null)
            {
                //TODO Deserialise error!
                return;
            }
            
            //Load them in
            foreach (SubscribedTopic topic in topics)
            {
                string[] parts = topic.ServerUrl.Split("://", 2);
                
                switch (parts[0].ToLower())
                {
                    case "ws":
                    case "wss":
                        _notificationListener.SubscribeToTopicUsingWebsocket($"{topic.TopicId}@{topic.ServerUrl}", topic.TopicId, topic.ServerUrl, topic.Username, topic.Password);
                        break;
                    
                    case "http":
                    case "https":
                        _notificationListener.SubscribeToTopicUsingLongHttpJson($"{topic.TopicId}@{topic.ServerUrl}", topic.TopicId, topic.ServerUrl, topic.Username, topic.Password);
                        break;
                    
                    default:
                        continue;
                }
                
                notificationTopics.Items.Add($"{topic.TopicId}@{topic.ServerUrl}");
            }
        }

        private SettingsModel GetDefaultSettings() => new()
        {
            Timeout = 5
        };

        private void LoadSettings()
        {
            string settingsFilePath = GetSettingsFilePath();
            
            //Check if we have any settings file on disk to load. If we don't, initialise defaults
            if (!File.Exists(settingsFilePath))
            {
                Program.Settings = GetDefaultSettings();
                
                SaveSettingsToFile();

                return;
            }

            //We have a settings file. Load it!
            string settingsSerialised = File.ReadAllText(settingsFilePath);

            //Check if the file is empty. If it is, initialise default settings
            if (string.IsNullOrWhiteSpace(settingsSerialised))
            {
                Program.Settings = GetDefaultSettings();
                
                SaveSettingsToFile();

                return;
            }

            //Deserialise the settings
            SettingsModel? settings = JsonConvert.DeserializeObject<SettingsModel?>(settingsSerialised);

            //Check if the deserialise succeeded. If it didn't, initialise default settings
            if (settings is null)
            {
                Program.Settings = GetDefaultSettings();
                
                SaveSettingsToFile();

                return;
            }

            Program.Settings = settings;
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            notifyIcon.Dispose();
        }
        
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Let it close
            if (_trueExit) return;

            if (e.CloseReason != CloseReason.UserClosing) return;
            
            Visible = false;
            e.Cancel = true;
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            _trueExit = true;
            Close();
        }

        private void ntfyshWebsiteToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://ntfy.sh/")
            {
                UseShellExecute = true
            });
        }

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using AboutBox d = new AboutBox();
            d.ShowDialog();
        }

        private void exitToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            _trueExit = true;
            Close();
        }
    }
}
