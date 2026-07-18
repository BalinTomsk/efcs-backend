namespace OWMService
{
    partial class ProjectInstaller
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.serviceProcessInstallerRWS = new System.ServiceProcess.ServiceProcessInstaller();
            this.serviceInstallerRWS = new System.ServiceProcess.ServiceInstaller();
            // 
            // serviceProcessInstallerRWS
            // 
            this.serviceProcessInstallerRWS.Account = System.ServiceProcess.ServiceAccount.LocalSystem;
            this.serviceProcessInstallerRWS.Password = null;
            this.serviceProcessInstallerRWS.Username = null;
            // 
            // serviceInstallerRWS
            // 
            this.serviceInstallerRWS.Description = "Get Open Weather Data";
            this.serviceInstallerRWS.DisplayName = "Open Weather Service";
            this.serviceInstallerRWS.ServiceName = "OWMService";
            // 
            // ProjectInstaller
            // 
            this.Installers.AddRange(new System.Configuration.Install.Installer[] {
            this.serviceProcessInstallerRWS,
            this.serviceInstallerRWS});

        }

        #endregion

        private System.ServiceProcess.ServiceProcessInstaller serviceProcessInstallerRWS;
        private System.ServiceProcess.ServiceInstaller serviceInstallerRWS;
    }
}