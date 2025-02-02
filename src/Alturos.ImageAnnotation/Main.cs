﻿using Alturos.ImageAnnotation.Contract;
using Alturos.ImageAnnotation.Contract.Amazon;
using Alturos.ImageAnnotation.Forms;
using Alturos.ImageAnnotation.Helper;
using Alturos.ImageAnnotation.Model;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Alturos.ImageAnnotation
{
    public partial class Main : Form
    {
        private readonly IAnnotationPackageProvider _annotationPackageProvider;
        private readonly AnnotationConfig _annotationConfig;
        private bool _changedPackage;
        private AnnotationPackage _selectedPackage;
        private bool _showAnnotated;

        public Main()
        {
            this.StartPosition = FormStartPosition.CenterScreen;
            try
            {
                this._annotationPackageProvider = new AmazonAnnotationPackageProvider();
            }
            catch (TaskCanceledException)
            {
                MessageBox.Show("The local database took too long to respond.\n\n" +
                    "Make sure your config is correctly setup. Are MinIO and your local DynamoDB running?\n\n" +
                    "Refer to the README.md for further information on how to correctly setup a local database.",
                    "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                this.Load += (s, e) => this.Close();
                return;
            }

            this._annotationConfig = this._annotationPackageProvider.GetAnnotationConfigAsync().GetAwaiter().GetResult();
            if (this._annotationConfig == null)
            {
                this._annotationConfig = new AnnotationConfig();

                using (var configurationDialog = new ConfigurationDialog())
                {
                    configurationDialog.Setup(this._annotationConfig);
                    var dialogResult = configurationDialog.ShowDialog();

                    if (dialogResult == DialogResult.OK)
                    {
                        this._annotationPackageProvider.SetAnnotationConfigAsync(this._annotationConfig);
                    }
                    else
                    {
                        this.Load += (s, e) => this.Close();
                        return;
                    }
                }
            }

            this.InitializeComponent();

            this.Text = $"Alturos Image Annotation {Application.ProductVersion}";
            this.downloadControl.Dock = DockStyle.Fill;

            this.annotationPackageListControl.Setup(this._annotationPackageProvider);
            this.annotationImageListControl.Setup(this._annotationPackageProvider);

            this.autoplaceAnnotationsToolStripMenuItem.Checked = false;

            this.annotationDrawControl.AutoplaceAnnotations = false;
            this.annotationDrawControl.SetObjectClasses(this._annotationConfig.ObjectClasses);
            this.annotationDrawControl.SetLabelsVisible(false);

            this.tagEditControl.SetConfig(this._annotationConfig);
        }

        #region Form Events

        private void Main_Load(object sender, EventArgs e)
        {
            Task.Run(async () => await this.LoadPackagesAsync(this._showAnnotated));
            this.RegisterEvents();
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            var confirmClosing = this.ConfirmDiscardingUnsavedChanges();
            if (!confirmClosing)
            {
                e.Cancel = true;
            }
        }

        private void Main_FormClosed(object sender, FormClosedEventArgs e)
        {
            this.UnregisterEvents();
        }

        #endregion

        #region Custom Control linking

        private void RegisterEvents()
        {
            this.annotationPackageListControl.PackageSelected += this.PackageSelected;
            this.annotationPackageListControl.CategorySelected += this.CategorySelected;
            this.annotationPackageListControl.DirtyUpdated += this.DirtyUpdated;
            this.annotationPackageListControl.ChangeToImageList += this.ChangeToImageList;

            this.annotationImageListControl.ImageSelected += this.ImageSelected;
            this.downloadControl.DownloadRequested += this.DownloadRequestedAsync;

            this.annotationDrawControl.ImageEdited += this.ImageEdited;

            this.KeyDown += this.annotationDrawControl.OnKeyDown;
            this.KeyUp += this.annotationDrawControl.OnKeyUp;
        }

        private void UnregisterEvents()
        {
            this.annotationPackageListControl.PackageSelected -= this.PackageSelected;
            this.annotationPackageListControl.CategorySelected -= this.CategorySelected;
            this.annotationPackageListControl.DirtyUpdated -= this.DirtyUpdated;
            this.annotationPackageListControl.ChangeToImageList -= this.ChangeToImageList;

            this.annotationImageListControl.ImageSelected -= this.ImageSelected;
            this.downloadControl.DownloadRequested -= this.DownloadRequestedAsync;

            this.annotationDrawControl.ImageEdited -= this.ImageEdited;

            this.KeyDown -= this.annotationDrawControl.OnKeyDown;
            this.KeyUp -= this.annotationDrawControl.OnKeyUp;
        }

        #endregion

        #region Closing Dialog

        private bool ConfirmDiscardingUnsavedChanges()
        {
            var unsyncedPackages = this.annotationPackageListControl.GetAllPackages().Where(o => o.IsDirty);
            if (unsyncedPackages.Any())
            {
                using (var dialog = new CloseConfirmationDialog())
                {
                    dialog.StartPosition = FormStartPosition.CenterParent;

                    var dialogResult = dialog.ShowDialog(this);
                    if (dialogResult == DialogResult.Cancel)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        #endregion

        #region Main Menu

        private void EnableMainMenu(bool enable)
        {
            this.Invoke((MethodInvoker)delegate
            {
                var showEditControls = enable && this._selectedPackage != null;
                var showDownloadControl = showEditControls && !this._selectedPackage.AvailableLocally;

                this.menuStripMain.Enabled = enable;
                this.annotationPackageListControl.Enabled = enable;
                this.tabControl.Enabled = enable;

                this.annotationImageListControl.Visible = showEditControls;
                this.annotationDrawControl.Visible = showEditControls;

                this.downloadControl.Visible = showDownloadControl;
            });
        }

        private async void RefreshToolStripMenuItem_Click(object sender, EventArgs e)
        {
            await this.LoadPackagesAsync(this._showAnnotated);
        }

        private async void SyncToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var packages = this.annotationPackageListControl.GetAllPackages().Where(o => o.IsDirty).ToArray();
            if (packages.Length == 0)
            {
                MessageBox.Show("There are no unchanged packages to sync.", "Nothing to sync!");
                return;
            }

            // Proceed with syncing
            using (var dialog = new SyncConfirmationDialog())
            {
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.SetUnsyncedPackages(packages.ToList());

                var dialogResult = dialog.ShowDialog(this);
                if (dialogResult != DialogResult.OK)
                {
                    return;
                }
            }

            using (var syncDialog = new SyncProgressDialog(this._annotationPackageProvider))
            {
                syncDialog.StartPosition = FormStartPosition.CenterParent;
                syncDialog.Show(this);

                await syncDialog.Sync(packages);
                syncDialog.Dispose();

                this.annotationPackageListControl.RefreshDataGrid();
            }
        }

        private void ExportToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new ExportDialog(this._annotationPackageProvider))
            {
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ShowDialog(this);
            }
        }

        private async void AddPackageStripMenuItem_Click(object sender, EventArgs e)
        {
            var uploadDialog = new AddPackageDialog(this._annotationPackageProvider);
            uploadDialog.StartPosition = FormStartPosition.CenterParent;

            var dialogResult = uploadDialog.ShowDialog();
            if (dialogResult == DialogResult.OK && !this._showAnnotated)
            {
                await this.LoadPackagesAsync(this._showAnnotated);
            }
        }

        private void AutoplaceAnnotationsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.autoplaceAnnotationsToolStripMenuItem.Checked = !this.autoplaceAnnotationsToolStripMenuItem.Checked;
            this.annotationDrawControl.AutoplaceAnnotations = this.autoplaceAnnotationsToolStripMenuItem.Checked;
        }

        private void ShowLabelsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.showLabelsToolStripMenuItem.Checked = !this.showLabelsToolStripMenuItem.Checked;
            this.annotationDrawControl.SetLabelsVisible(this.showLabelsToolStripMenuItem.Checked);
        }

        private async void SettingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new ConfigurationDialog())
            {
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.Setup(this._annotationConfig);
                var dialogResult = dialog.ShowDialog(this);
                if (dialogResult == DialogResult.OK)
                {
                    await this._annotationPackageProvider.SetAnnotationConfigAsync(this._annotationConfig);
                }
            }
        }

        private void HelpToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dialog = new HelpDialog())
            {
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ShowDialog(this);
            }
        }

        #endregion

        #region Logic

        private void ChangeToImageList()
        {
            this.annotationImageListControl.Focus();
        }

        private async Task LoadPackagesAsync(bool annotated)
        {
            this.EnableMainMenu(false);
            this.tabControl.Invoke((MethodInvoker)delegate { this.tabControl.SelectedIndex = 0; });
            await this.annotationPackageListControl.LoadPackagesAsync(annotated);
            this.EnableMainMenu(true);
        }

        private void PackageSelected(AnnotationPackage package, PackageSelectionBehavior behavior)
        {
            switch (behavior)
            {
                case PackageSelectionBehavior.RefreshOnly:
                    if (this._selectedPackage != package)
                    {
                        return;
                    }
                    break;
                case PackageSelectionBehavior.SwitchOnly:
                    if (this._selectedPackage == package)
                    {
                        return;
                    }
                    break;
            }

            // Cancel if multiple packages are selected
            if (this.annotationPackageListControl.GetSelectedPackageCount() > 1)
            {
                this.SetPackageEditingControlsEnabled(false);
                return;
            }
            
            this.SetPackageEditingControlsEnabled(true);

            this._changedPackage = true;
            this._selectedPackage = package;

            this.annotationImageListControl.Hide();
            this.downloadControl.Hide();

            this.annotationImageListControl.Reset();
            this.annotationDrawControl.Reset();

            this.labelUserName.Text = package?.User;
            
            if (package != null)
            {
                this.tagEditControl.SetTags(package);

                if (package.AvailableLocally)
                {
                    this.annotationImageListControl.SetPackage(package);
                    this.annotationImageListControl.Show();

                    this.annotationPackageListControl.RefreshDataGrid();
                }
                else
                {
                    this.downloadControl.ShowDownloadDialog(package);
                }
            }

            this._changedPackage = false;
        }

        private async void CategorySelected(AnnotationCategory category)
        {
            switch (category)
            {
                case AnnotationCategory.Unannotated:
                    this._showAnnotated = false;
                    break;
                case AnnotationCategory.Annotated:
                    this._showAnnotated = true;
                    break;
            }

            await this.LoadPackagesAsync(this._showAnnotated);
        }

        private void DirtyUpdated(bool dirty)
        {
            this.menuStripMain.Invoke((MethodInvoker)delegate
            {
                this.syncToolStripMenuItem.Enabled = dirty;
            });

            this.Invoke((MethodInvoker)delegate
            {
                Text = $"{this.Text.Replace("*", string.Empty)}{(dirty ? "*" : string.Empty)}";
            });
        }

        private void SetPackageEditingControlsEnabled(bool enabled)
        {
            this.annotationImageListControl.Visible = enabled;
            this.tagEditControl.Visible = enabled;
            this.groupBoxInfo.Visible = enabled;
            this.annotationDrawControl.Visible = enabled;
            this.downloadControl.Visible = enabled;
        }

        private void ImageSelected(AnnotationImage image)
        {
            this.annotationDrawControl.SetImage(image);

            // Failsafe, because ImageSelected is triggered when the package is changed. We don't want to select the image in this case.
            if (this._changedPackage)
            {
                return;
            }

            if (image == null)
            {
                return;
            }

            this.annotationDrawControl.ApplyCachedBoundingBox();
        }

        private void ImageEdited(AnnotationImage annotationImage)
        {
            if (annotationImage == null)
            {
                return;
            }

            annotationImage.Package.IsDirty = true;
            annotationImage.Package.UpdateAnnotationStatus(annotationImage);

            this.annotationPackageListControl.RefreshDataGrid();
        }

        private async Task DownloadRequestedAsync(AnnotationPackage package)
        {
            await this._annotationPackageProvider.DownloadPackageAsync(package);

            this.PackageSelected(package, PackageSelectionBehavior.RefreshOnly);
        }

        #endregion
    }
}
