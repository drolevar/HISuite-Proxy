using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;
using System.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography;
using System.IO.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProxyDiagnostics;

namespace HiSuite_Proxy
{
    public partial class Form1 : Form
    {
        ProxyServer proxyserver = new ProxyServer();      
        ExplicitProxyEndPoint endpoint = new ExplicitProxyEndPoint(IPAddress.Any, 7777);
        public class CustomData
        {
            public bool CustomBase = false, CustomPreload = false, CustomCust = false, LocalBase = false, LocalPreload = false, LocalCust = false;
            public string CustomBaseID, CustomPreloadID, CustomCustID, LocalBaseDir, LocalPreloadDir, LocalCustDir;
        }
        public CustomData _customData = new CustomData();
        FirmFinder firmFinder = null;

        private class PackageData
        {
            public string 
                PackageFile,
                PackageSize,
                PackageName,
                PackageMD5,
                PackageSha256;
            public PackageData(string PackageFile, string PackageName, string PackageSize, string PackageMD5, string PackageSha256)
            {
                this.PackageFile = PackageFile;
                this.PackageName = PackageName;
                this.PackageSize = PackageSize;
                this.PackageMD5 = PackageMD5;
                this.PackageSha256 = PackageSha256;
            }
        }
        PackageData basePKGData = null, custPKGData = null, preloadPGKData = null;
        public Form1(string[] arguments)
        {
            if (arguments.Length > 0 && arguments.Length == 2)
            {
                ReplaceComponent(arguments[0], arguments[1]);
                Environment.Exit(Environment.ExitCode);
                return;
            }
            else if(arguments.Length == 1 && arguments[0] == "SETUP")
            {
                SetUP setUP = new SetUP(this);
                setUP.ShowDialog();
                this.Load += delegate
                {
                    this.Close();
                };
                return;
            }
            InitializeComponent();
            if (Environment.GetEnvironmentVariable("HISUITE_PROXY_FIXED_OFFER_PREVIEW") == "1")
                Text = "HiSuite Proxy - METADATA PREVIEW - downloads blocked";
            Log.Event("session.entry", "Proxy source build with redacted file diagnostics.");
            Log.Checkpoint(this, "ui.ready");

            textBox8.TextAlign = HorizontalAlignment.Center;

            firmFinder = new FirmFinder(this);

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback(delegate { return true; });

            this.FormClosing += delegate
            {
                try
                {
                    proxyserver.Stop();
                    Environment.Exit(Environment.ExitCode);
                }
                catch
                {

                }
            };
            try
            {
                proxyserver.CertificateManager.CreateRootCertificate(true);
                proxyserver.CertificateManager.TrustRootCertificate(true);

                proxyserver.ServerCertificateValidationCallback += Proxyserver_ServerCertificateValidationCallback;
                proxyserver.BeforeRequest += DiagnosticsBeforeRequest;
                proxyserver.BeforeResponse += DiagnosticsBeforeResponse;

                proxyserver.AddEndPoint(endpoint);
                
                proxyserver.Start();
            }
            catch (Exception ex)
            {
                Log.ObserveException("proxy.start.failed", ex);
                textBox3.AppendText(ex.StackTrace);
                //MessageBox.Show(ex.Message, "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            textBox3.ScrollBars = ScrollBars.Both;
            textBox1.TextChanged += delegate
            {
                string text = textBox1.Text;
                int where = text.IndexOf("/full");
                if (where != -1)
                {
                    textBox1.Text = text.Substring(0, ++where);
                }
                if(!text.Contains("/TDS/data/files"))
                {
                    textBox9.Enabled = true;
                }
                else
                {
                    textBox9.Enabled = false;
                }
            };
            textBox4.TextChanged += delegate
            {
                string text = textBox4.Text;
                int where = text.IndexOf("/full");
                if (where != -1)
                {
                    textBox4.Text = text.Substring(0, ++where);
                }

                if (!text.Contains("/TDS/data/files"))
                {
                    textBox11.Enabled = true;
                }
                else
                {
                    textBox11.Enabled = false;
                }
            };

            textBox7.TextChanged += delegate
            {
                string text = textBox7.Text;
                int where = text.IndexOf("/full");
                if (where != -1)
                {
                    textBox7.Text = text.Substring(0, ++where);
                }
                if (!text.Contains("/TDS/data/files"))
                {
                    textBox10.Enabled = true;
                }
                else
                {
                    textBox10.Enabled = false;
                }
            };
        }

        private sealed class RequestDiagnosticsContext
        {
            public Form1 form;
            public Titanium.Web.Proxy.EventArguments.SessionEventArgs e;
        }

        private Task DiagnosticsBeforeRequest(object sender, Titanium.Web.Proxy.EventArguments.SessionEventArgs e)
        {
            Log.Checkpoint(new RequestDiagnosticsContext { form = this, e = e }, "request.handler.begin");
            return TitaniumHooks.AfterRequest(FixedOfferPreview.Dispatch(this, sender, e), e);
        }

        private Task DiagnosticsBeforeResponse(object sender, Titanium.Web.Proxy.EventArguments.SessionEventArgs e)
        {
            return TitaniumHooks.AfterResponse(Proxyserver_BeforeResponse(sender, e), e);
        }

        private volatile string lastObservedHandsetCVersion = null;

        internal static string NormalizeStandaloneBaseManifest(string xml)
        {
            if (String.IsNullOrEmpty(xml)) return xml;
            return xml.Replace("<GROUP version=\"G1.0\"/>", "<GROUP version=\"G2.0\"/>")
                      .Replace("<GBCINFO name=\"other\" version=\"NA\"/>", "<GBCINFO name=\"other\" version=\"GBC1.0\"/>");
        }

        private bool ShouldNormalizeStandaloneBaseManifest()
        {
            if (checkBox3.Checked || checkBox1.Checked) return false;
            string cver = lastObservedHandsetCVersion;
            if (String.IsNullOrWhiteSpace(cver)) return true;
            // EMUI 8.x and 9.x CUST versions use G1.0/NA; EMUI 10+ (10.x, 11.x, 12.x, 101.x, 102.x) use G2.0/GBC1.0.
            if (cver.IndexOf(" 8.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cver.IndexOf(" 9.", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            return true;
        }

        private async Task Proxyserver_BeforeResponse(object sender, Titanium.Web.Proxy.EventArguments.SessionEventArgs e)
        {
            string reqeustURL = e.HttpClient.Request.Url;

            if (reqeustURL.IndexOf("filelist.xml", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (!_customData.LocalBase && basePKGData == null &&
                    ShouldNormalizeStandaloneBaseManifest() &&
                    FirmwarePaths.MatchesRequest(reqeustURL, textBox1.Text))
                {
                    string response = await TitaniumHooks.GetResponseBodyAsString(e, CancellationToken.None);
                    string normalized = NormalizeStandaloneBaseManifest(response);
                    if (!String.Equals(normalized, response, StringComparison.Ordinal))
                    {
                        e.SetResponseBodyString(normalized);
                        Log.ObserveResponse("manifest.coupling.normalized", reqeustURL, normalized);
                    }
                }
                else if (_customData.LocalBase || _customData.LocalCust || _customData.LocalPreload)
                {
                    if (_customData.LocalBase && FirmwarePaths.MatchesRequest(reqeustURL, textBox1.Text))
                    {
                        string response = await TitaniumHooks.GetResponseBodyAsString(e, CancellationToken.None);
                        if (ShouldNormalizeStandaloneBaseManifest())
                        {
                            string normalized = NormalizeStandaloneBaseManifest(response);
                            if (!String.Equals(normalized, response, StringComparison.Ordinal))
                            {
                                e.SetResponseBodyString(normalized);
                                Log.ObserveResponse("manifest.coupling.normalized", reqeustURL, normalized);
                                response = normalized;
                            }
                        }
                        int where = response.IndexOf("package=");
                        if (where == -1)
                        {
                            MessageBox.Show("Couldn't load base package name, please contact developer if error continues", "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            where += 9;
                            int finish = response.IndexOf('"', where);
                            CopyFile(textBox2.Text, _customData.LocalBaseDir, response.Substring(where, finish - where), 0);
                        }
                    }
                    else if (_customData.LocalCust && FirmwarePaths.MatchesRequest(reqeustURL, textBox7.Text))
                    {
                        string response = await TitaniumHooks.GetResponseBodyAsString(e, CancellationToken.None);
                        int where = response.IndexOf("package=");
                        if (where == -1)
                        {
                            MessageBox.Show("Couldn't load cust package name, please contact developer if error continues", "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            where += 9;
                            int finish = response.IndexOf('"', where);
                            CopyFile(textBox6.Text, _customData.LocalCustDir, response.Substring(where, finish - where), 1);
                        }
                    }
                    else if (_customData.LocalPreload && FirmwarePaths.MatchesRequest(reqeustURL, textBox4.Text))
                    {
                        string response = await TitaniumHooks.GetResponseBodyAsString(e, CancellationToken.None);
                        int where = response.IndexOf("package=");
                        if (where == -1)
                        {
                            MessageBox.Show("Couldn't load preload package name, please contact developer if error continues", "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                        else
                        {
                            where += 9;
                            int finish = response.IndexOf('"', where);
                            CopyFile(textBox5.Text, _customData.LocalPreloadDir, response.Substring(where, finish - where), 2);
                        }
                    }
                }
            }
        }

        internal string ResolveConfiguredVersionId(string url)
        {
            if (FirmwarePaths.MatchesRequest(url.TrimEnd('/') + "/full/filelist.xml", textBox1.Text))
                return _customData.CustomBase ? _customData.CustomBaseID : GetURLVersion(textBox1.Text, 0);
            if (FirmwarePaths.MatchesRequest(url.TrimEnd('/') + "/full/filelist.xml", textBox7.Text))
                return _customData.CustomCust ? _customData.CustomCustID : GetURLVersion(textBox7.Text, 1);
            if (FirmwarePaths.MatchesRequest(url.TrimEnd('/') + "/full/filelist.xml", textBox4.Text))
                return _customData.CustomPreload ? _customData.CustomPreloadID : GetURLVersion(textBox4.Text, 2);
            return GetURLVersion(url, 0);
        }
        private string GetURLVersion(string url, int type = 0)
        {
            if(url.Contains("/TDS/data/files"))
            {
                int where = url.IndexOf("/v");
                if (where == -1)
                {
                    return "Unknown";
                }
                else
                {
                    where += 2;
                    int finish = url.IndexOf('/', where);
                    return url.Substring(where, finish - where);
                }
            }
            else
            {
                if (type == 0)
                    return (textBox9.Text == "0") ? ("Unknown") : (textBox9.Text);
                else if (type == 1) // CUST
                    return (textBox10.Text == "0") ? ("Unknown") : (textBox10.Text);
                else if (type == 2) // Preload
                    return (textBox11.Text == "0") ? ("Unknown") : (textBox11.Text);
                else
                    return "Unknown";
            }
            
        }
        private async Task Proxyserver_BeforeRequest(object sender, Titanium.Web.Proxy.EventArguments.SessionEventArgs e)
        {
            try
            {
                string reqeustURL = e.HttpClient.Request.Url;
                if (reqeustURL.EndsWith(":7777/getFile.txt"))
                {
                    string Body = await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                    RomDetails.gZipWebClient client = new RomDetails.gZipWebClient();
                    string responseBody = "";
                    try
                    {
                        responseBody = client.DownloadString(Body);
                    }
                    catch
                    {

                    }
                    Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                    Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                    Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));
                    TitaniumHooks.Ok(e, responseBody, Headers);
                }
                else if (reqeustURL.EndsWith(":7777/checkRom.txt"))
                {
                    string Body = await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                    Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                    Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                    Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));

                    bool isapprovedForInstallation;
                    try
                    {
                        isapprovedForInstallation = RomDetails.ApprovedForInstallation(Body);
                    }
                    catch
                    {
                        isapprovedForInstallation = false;
                    }
                    if (isapprovedForInstallation)
                    {
                        TitaniumHooks.Ok(e, "YES", Headers);
                    }
                    else
                    {
                        TitaniumHooks.Ok(e, "NO", Headers);
                    }
                }
                else if (reqeustURL.EndsWith(":7777/addROM.txt"))
                {
                    if(e.HttpClient.Request.HasBody)
                    {
                        string Body = await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                        string[] BodyData = Body.Split('|');
                        if(BodyData.Length > 2)
                        {
                            if (BodyData[1].Contains("-PRELOAD "))
                            {
                                textBox4.Text = BodyData[2];
                                textBox5.Text = BodyData[1];
                                textBox11.Text = BodyData[0];
                                checkBox1.Checked = true;
                                //Activate();

                                Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                                Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                                Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));
                                TitaniumHooks.Ok(e, "OK", Headers);
                            }
                            else if (BodyData[1].Contains("-CUST "))
                            {
                                textBox7.Text = BodyData[2];
                                textBox6.Text = BodyData[1];
                                textBox10.Text = BodyData[0];
                                checkBox3.Checked = true;
                                //Activate();

                                Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                                Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                                Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));
                                TitaniumHooks.Ok(e, "OK", Headers);
                            }
                            else
                            {
                                textBox1.Text = BodyData[2];
                                textBox2.Text = BodyData[1];
                                textBox9.Text = BodyData[0];
                                checkBox3.Checked = false;
                                checkBox1.Checked = false;
                                //Activate();

                                Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                                Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                                Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));
                                TitaniumHooks.Ok(e, "OK", Headers);
                            }
                        }
                        else
                        {
                            Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                            Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                            Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));
                            TitaniumHooks.Ok(e, "ERROR", Headers);
                        }
                    }
                    else
                    {
                        Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                        Headers.Add("Access-Control-Allow-Origin", new HttpHeader("Access-Control-Allow-Origin", e.HttpClient.Request.Headers.Headers["Origin"].Value));
                        Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain"));
                        TitaniumHooks.Ok(e, "ERROR", Headers);
                    }
                    return;
                }
                if (reqeustURL.Contains("query.hicloud.com") || reqeustURL.Contains("/TDS/data/files") || reqeustURL.Contains("dbankcdn.com") || reqeustURL.Contains("hicloud.com"))
                {
                    this.Invoke(new Action(() =>
                    {
                        textBox3.AppendText(e.HttpClient.Request.Url + Environment.NewLine);
                    }));
                    if (checkBox2.Checked)
                    {
                        string debug = e.HttpClient.Request.Url + " : " + Environment.NewLine;
                        List<HttpHeader> clientheaders = e.HttpClient.Request.Headers.GetAllHeaders();
                        for (int i = 0, j = clientheaders.Count; i < j; i++)
                        {
                            debug += clientheaders[i].Name + ": " + clientheaders[i].Value + Environment.NewLine;
                        }
                        if (e.HttpClient.Request.HasBody)
                        {
                            debug += Environment.NewLine + await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                        }
                        debug += Environment.NewLine + Environment.NewLine;
                        File.AppendAllText("logs.txt", debug);
                    }
                }
                if (reqeustURL.Contains("checkHiSuiteConnection"))
                {
                    TitaniumHooks.Ok(e, "Success");
                }
                else if (reqeustURL.Contains("query.hicloud.com"))
                {
                    if (reqeustURL.Contains("CheckNewVersion.aspx"))
                    {
                        Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                        Headers.Add("Content-Type", new HttpHeader("Content-Type", "application/xml;charset=UTF-8"));
                        TitaniumHooks.Ok(e, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><root><status>1</status></root>", Headers, true);
                    }
                    else if (reqeustURL.Contains("CouplingReport.action"))
                    {
                        string resbody = await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                        int where = resbody.IndexOf("descinfo");
                        if (where != -1)
                        {
                            where += 13;
                            int finish = resbody.IndexOf("\",", where);
                            new Thread(() =>
                            {
                                MessageBox.Show(resbody.Substring(where, finish - where), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }).Start();
                        }
                    }
                    else if (reqeustURL.Contains("authorize.action"))
                    {
                        WebClient client = new WebClient();
                        client.Headers.Set(HttpRequestHeader.Accept, "*/*");
                        client.Headers.Set(HttpRequestHeader.ContentType, "application/json;charset=UTF-8");
                        string updata = await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                        string respons = "0";
                        bool forceAuthBridge = checkBox7.Checked;
                        if (forceAuthBridge)
                        {
                            int where = updata.IndexOf("\"deviceCertificate");
                            if (where != -1)
                            {
                                int finish = updata.IndexOf("\",", where) + 2;
                                finish = updata.IndexOf('"', finish);
                                updata = updata.Remove(where, finish - where);
                            }

                            where = updata.IndexOf("\"keyAttestation");
                            if (where != -1)
                            {
                                int finish = updata.IndexOf("\",", where) + 2;
                                finish = updata.IndexOf('"', finish);
                                updata = updata.Remove(where, finish - where);
                            }
                        }
                        if (updata.Contains("\"2\"") && !updata.Contains("deviceCertificate"))
                        {
                            if (forceAuthBridge || MessageBox.Show("Apparently your phone is soft re-branded so normal authentication is going to fail!\r\n\r\nDo you want to use authentication bridge?", "Alert", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK)
                            {
                                AuthBridge authenticate = new AuthBridge(updata);
                                authenticate.ShowDialog();
                                if (authenticate.Success)
                                {
                                    respons = authenticate.responsedata;
                                }
                                else
                                {
                                    respons = "0";
                                }
                            }
                            else
                            {
                                respons = "0";
                            }
                        }
                        if (respons == "0")
                        {
                            try
                            {
                                respons = Log.UploadString(client, "https://query.hicloud.com:443/sp_ard_common/v1/authorize.action", updata);
                            }
                            catch (Exception ex)
                            {
                                respons = null;
                                new Thread(() =>
                                {
                                    MessageBox.Show(ex.Message + "\r\n\r\n Please check your internet connection", "Authorization Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }).Start();
                            }
                        }
                        Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                        if (respons == null)
                            respons = "";
                        Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/plain;charset=UTF-8"));
                        Headers.Add("Server", new HttpHeader("Server", "elb"));
                        Headers.Add("X-XSS-Protection", new HttpHeader("X-XSS-Protection", "1; mode=block"));
                        Headers.Add("X-frame-options", new HttpHeader("X-frame-options", "SAMEORIGIN"));
                        Headers.Add("X-Content-Type-Options", new HttpHeader("X-Content-Type-Options", "nosniff"));
                        if (respons.Length > 2)
                        {
                            if (!CheckAuthentication(updata, respons))
                            {
                                respons = "";
                            }
                        }
                        TitaniumHooks.Ok(e, respons, Headers);
                    }
                    else if (e.HttpClient.Request.HasBody)
                    {
                        string bodydata = await TitaniumHooks.GetRequestBodyAsString(e, CancellationToken.None);
                        JObject requestJson;
                        string pacakgetype = ExtractPackageType(bodydata, out requestJson);
                        if (!String.IsNullOrEmpty(pacakgetype))
                        {
                            if (textBox2.Text.Length < 2)
                                textBox2.Text = "Unknown";
                            if (textBox6.Text.Length < 2)
                                textBox6.Text = "Unknown";
                            if (textBox5.Text.Length < 2)
                                textBox5.Text = "Unknown";
                            string opscheck = "full";
                            if (radioButton2.Checked)
                            {
                                opscheck = "hfull_switch";
                            }
                            else if (radioButton3.Checked)
                            {
                                opscheck = "full_back";
                            }
                            else if (radioButton4.Checked)
                            {
                                opscheck = "full_recovery";
                            }
                            /*if((checkBox9.Checked || checkBox8.Checked) && checkBox10.Checked)
                            {
                                opscheck = "increment";
                            }*/
                            if (pacakgetype == opscheck || (radioButton4.Checked && (pacakgetype == "full_indicator" || pacakgetype == "full")))
                            {
                                string responsedata = Encoding.UTF8.GetString(Properties.Resources.responsedata).Replace("\r\n", "");
                                /*if(checkBox9.Checked) // EMUI 7
                                {
                                    responsedata = Encoding.UTF8.GetString(Properties.Resources.emui7resp).Replace("\r\n", "");
                                    bool Iveabase = (GetURLVersion(textBox1.Text, 0) != "Unknown");
                                    if (Iveabase)
                                    {
                                        if (_customData.CustomBase)
                                        {
                                            responsedata = responsedata.Replace("WriteVerionID", _customData.CustomBaseID);
                                        }
                                        else
                                        {
                                            responsedata = responsedata.Replace("WriteVerionID", GetURLVersion(textBox1.Text, 0));
                                        }
                                        if (checkBox4.Checked)
                                            responsedata = responsedata.Replace("pointbase", "1");
                                        else
                                            responsedata = responsedata.Replace("pointbase", "0");

                                        responsedata = responsedata.Replace("basetype", textBox8.Text);
                                        responsedata = responsedata.Replace("VersionURL", textBox1.Text);
                                        responsedata = responsedata.Replace("Unknown1", textBox2.Text);
                                    }
                                }*/
                                if(checkBox8.Checked) // Old Firmware
                                {
                                    responsedata = Encoding.UTF8.GetString(Properties.Resources.oldresponse).Replace("\r\n", "");
                                    bool Iveabase = (GetURLVersion(textBox1.Text, 0) != "Unknown");
                                    if (Iveabase)
                                    {
                                        responsedata = responsedata.Replace("hasfullpackage", "0");
                                        if (_customData.CustomBase)
                                        {
                                            responsedata = responsedata.Replace("WriteVerionID", _customData.CustomBaseID);
                                        }
                                        else
                                        {
                                            responsedata = responsedata.Replace("WriteVerionID", GetURLVersion(textBox1.Text, 0));
                                        }
                                        if (checkBox4.Checked)
                                            responsedata = responsedata.Replace("pointbase", "1");
                                        else
                                            responsedata = responsedata.Replace("pointbase", "0");

                                        responsedata = responsedata.Replace("basetype", textBox8.Text);
                                        responsedata = responsedata.Replace("VersionURL", textBox1.Text);
                                        responsedata = responsedata.Replace("Unknown1", textBox2.Text);
                                    }
                                    else
                                    {
                                        responsedata = responsedata.Replace("hasfullpackage", "1");
                                    }
                                }
                                else
                                {
                                    bool Iveabase = (GetURLVersion(textBox1.Text, 0) != "Unknown");
                                    if (Iveabase)
                                    {
                                        responsedata = responsedata.Replace("hasfullpackage", "0");
                                        if (_customData.CustomBase)
                                        {
                                            responsedata = responsedata.Replace("WriteVerionID", _customData.CustomBaseID);
                                        }
                                        else
                                        {
                                            responsedata = responsedata.Replace("WriteVerionID", GetURLVersion(textBox1.Text, 0));
                                        }
                                        if (checkBox4.Checked)
                                            responsedata = responsedata.Replace("pointbase", "1");
                                        else
                                            responsedata = responsedata.Replace("pointbase", "0");

                                        responsedata = responsedata.Replace("basetype", textBox8.Text);
                                        responsedata = responsedata.Replace("VersionURL", textBox1.Text);
                                        responsedata = responsedata.Replace("Unknown1", textBox2.Text);
                                    }
                                    else
                                    {
                                        responsedata = responsedata.Replace("hasfullpackage", "1");
                                    }

                                    if (checkBox1.Checked)
                                    {
                                        if (_customData.CustomPreload)
                                        {
                                            responsedata = responsedata.Replace("WiteVerionID", _customData.CustomPreloadID);
                                        }
                                        else
                                        {
                                            responsedata = responsedata.Replace("WiteVerionID", GetURLVersion(textBox4.Text, 2));
                                        }
                                        if (checkBox6.Checked)
                                        {
                                            if (Iveabase)
                                            {
                                                responsedata = responsedata.Replace("pointpreload", "2");
                                            }
                                            else
                                            {
                                                responsedata = responsedata.Replace("pointpreload", "1");
                                            }
                                        }
                                        else
                                            responsedata = responsedata.Replace("pointpreload", "0");
                                        responsedata = responsedata.Replace("VrsionURL", textBox4.Text);
                                        responsedata = responsedata.Replace("Unknown2", textBox5.Text);
                                        responsedata = responsedata.Replace("hasreloadedpackage", "0");
                                    }
                                    else
                                    {
                                        responsedata = responsedata.Replace("hasreloadedpackage", "1");
                                    }

                                    if (checkBox3.Checked)
                                    {
                                        if (_customData.CustomCust)
                                        {
                                            responsedata = responsedata.Replace("WteVerionID", _customData.CustomCustID);
                                        }
                                        else
                                        {
                                            responsedata = responsedata.Replace("WteVerionID", GetURLVersion(textBox7.Text, 1));
                                        }
                                        if (checkBox5.Checked)
                                        {
                                            if (Iveabase)
                                            {
                                                responsedata = responsedata.Replace("pointcust", "2");
                                            }
                                            else
                                            {
                                                responsedata = responsedata.Replace("pointcust", "1");
                                            }
                                        }
                                        else
                                            responsedata = responsedata.Replace("pointcust", "0");
                                        responsedata = responsedata.Replace("VrionURL", textBox7.Text);
                                        responsedata = responsedata.Replace("Unknown3", textBox6.Text);
                                        responsedata = responsedata.Replace("hascustpackage", "0");
                                    }
                                    else
                                    {
                                        responsedata = responsedata.Replace("hascustpackage", "1");
                                    }
                                }
                                responsedata = FinalizeCheckAndAuthorizeResponse(responsedata, requestJson);
                                Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                                Headers.Add("Content-Type", new HttpHeader("Content-Type", "application/json;charset=utf8"));
                                TitaniumHooks.Ok(e, responsedata, Headers, true);
                            }
                            else
                            {
                                string responsedata = Encoding.UTF8.GetString(Properties.Resources.emptyresponse).Replace("\r\n", "");
                                Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                                Headers.Add("Content-Type", new HttpHeader("Content-Type", "application/json;charset=utf8"));
                                TitaniumHooks.Ok(e, responsedata, Headers, true);
                            }
                        }
                    }
                }
                else if ((basePKGData != null && FirmwarePaths.MatchesRequest(reqeustURL, textBox1.Text)) ||
                     (custPKGData != null && FirmwarePaths.MatchesRequest(reqeustURL, textBox7.Text)) ||
                     (preloadPGKData != null && FirmwarePaths.MatchesRequest(reqeustURL, textBox4.Text)))
                {
                    if (reqeustURL.EndsWith("filelist.xml"))
                    {
                        if (basePKGData != null && FirmwarePaths.MatchesRequest(reqeustURL, textBox1.Text))
                        {
                            string responsefile = Properties.Resources.filelist.Replace("changelog", "changelog_base");
                            responsefile = responsefile.Replace("firmkind", "base");
                            responsefile = responsefile.Replace("firmfile", basePKGData.PackageFile);

                            responsefile = responsefile.Replace("firmMD5", basePKGData.PackageMD5);
                            responsefile = responsefile.Replace("firmSHA256", basePKGData.PackageSha256);
                            responsefile = responsefile.Replace("firmsize", basePKGData.PackageSize);

                            Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                            Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/xml"));

                            TitaniumHooks.Ok(e, responsefile, Headers, true);
                        }
                        else if (custPKGData != null && FirmwarePaths.MatchesRequest(reqeustURL, textBox7.Text))
                        {
                            string responsefile = Properties.Resources.filelist;
                            responsefile = responsefile.Replace("firmkind", "cust");
                            responsefile = responsefile.Replace("firmfile", custPKGData.PackageFile);

                            responsefile = responsefile.Replace("firmMD5", custPKGData.PackageMD5);
                            responsefile = responsefile.Replace("firmSHA256", custPKGData.PackageSha256);
                            responsefile = responsefile.Replace("firmsize", custPKGData.PackageSize);

                            Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                            Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/xml"));

                            TitaniumHooks.Ok(e, responsefile, Headers, true);
                        }
                        else if (preloadPGKData != null && FirmwarePaths.MatchesRequest(reqeustURL, textBox4.Text))
                        {
                            string responsefile = Properties.Resources.filelist;
                            responsefile = responsefile.Replace("firmkind", "preload");
                            responsefile = responsefile.Replace("firmfile", preloadPGKData.PackageFile);

                            responsefile = responsefile.Replace("firmMD5", preloadPGKData.PackageMD5);
                            responsefile = responsefile.Replace("firmSHA256", preloadPGKData.PackageSha256);
                            responsefile = responsefile.Replace("firmsize", preloadPGKData.PackageSize);

                            Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                            Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/xml"));

                            TitaniumHooks.Ok(e, responsefile, Headers, true);
                        }
                    }
                    else if (reqeustURL.EndsWith("changelog_base.xml") || reqeustURL.EndsWith("changelog.xml"))
                    {
                        Dictionary<string, HttpHeader> Headers = new Dictionary<string, HttpHeader>();
                        Headers.Add("Content-Type", new HttpHeader("Content-Type", "text/xml"));

                        TitaniumHooks.Ok(e, Properties.Resources.changelog, Headers, true);
                    }
                }
                
            }
            catch (Exception ex)
            {
                Log.ObserveException("request.handler.caught", ex);
                this.Invoke(new Action(() =>
                {
                    textBox3.AppendText(ex.StackTrace + Environment.NewLine);
                }));
                MessageBox.Show(ex.Message, "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private Task Proxyserver_ServerCertificateValidationCallback(object sender, Titanium.Web.Proxy.EventArguments.CertificateValidationEventArgs e)
        {
            e.IsValid = true;
            return Task.CompletedTask;
        }

        internal static string ExtractPackageType(string bodydata, out JObject requestJson)
        {
            requestJson = null;
            if (String.IsNullOrWhiteSpace(bodydata)) return null;
            try
            {
                requestJson = JObject.Parse(bodydata);
                string packageType = (string)requestJson["commonRules"]?["PackageType"];
                if (!String.IsNullOrEmpty(packageType)) return packageType;
            }
            catch
            {
                requestJson = null;
            }
            int keyIndex = bodydata.IndexOf("PackageType", StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colonIndex = bodydata.IndexOf(':', keyIndex + 11);
            if (colonIndex < 0) return null;
            int openQuote = bodydata.IndexOf('"', colonIndex + 1);
            if (openQuote < 0) return null;
            int closeQuote = bodydata.IndexOf('"', openQuote + 1);
            if (closeQuote < 0) return null;
            return bodydata.Substring(openQuote + 1, closeQuote - openQuote - 1);
        }

        private string lastObservedDeviceId = null;
        private string lastObservedUpdateToken = null;
        private string lastObservedAuthVendor = null;
        private string lastObservedImei = null;

        private string FinalizeCheckAndAuthorizeResponse(string responsedata, JObject requestJson)
        {
            try
            {
                if (requestJson != null)
                {
                    string cver = (string)requestJson["commonRules"]?["CVersion"];
                    if (!String.IsNullOrWhiteSpace(cver))
                        lastObservedHandsetCVersion = cver;
                    string devId = (string)requestJson["deviceId"];
                    if (!String.IsNullOrWhiteSpace(devId))
                        lastObservedDeviceId = devId;
                    string updTok = (string)requestJson["updateToken"];
                    if (!String.IsNullOrWhiteSpace(updTok))
                        lastObservedUpdateToken = updTok;
                    string vend = (string)requestJson["authVendor"];
                    if (!String.IsNullOrWhiteSpace(vend))
                        lastObservedAuthVendor = vend;
                    string obsImei = (string)requestJson["commonRules"]?["IMEI"];
                    if (!String.IsNullOrWhiteSpace(obsImei))
                        lastObservedImei = obsImei;
                }
                JObject responseObj = JObject.Parse(responsedata);
                if (responseObj["authorizeResult"] == null)
                    responseObj["authorizeResult"] = "";
                List<string> enabledVersionIds = new List<string>();
                JArray results = responseObj["versionPackageCheckResults"] as JArray;
                if (results != null)
                {
                    foreach (JToken token in results)
                    {
                        JObject item = token as JObject;
                        if (item == null) continue;
                        if ((string)item["status"] != "0")
                        {
                            item["components"] = new JArray();
                        }
                        else
                        {
                            JArray components = item["components"] as JArray;
                            if (components != null && components.Count > 0)
                            {
                                JObject comp = components[0] as JObject;
                                if (comp != null)
                                {
                                    if (comp["versionType"] == null)
                                        comp["versionType"] = textBox8.Text;
                                    string versionId = (string)comp["versionID"];
                                    if (!String.IsNullOrWhiteSpace(versionId) && versionId != "Unknown" && versionId != "0")
                                        enabledVersionIds.Add(versionId);
                                }
                            }
                        }
                    }
                }
                if (requestJson != null && enabledVersionIds.Count > 0)
                {
                    string inlineAuth = TryAcquireInlineAuthorization(requestJson, enabledVersionIds, enabledVersionIds.Count == 1);
                    if (String.IsNullOrEmpty(inlineAuth) && enabledVersionIds.Count > 1)
                    {
                        inlineAuth = TryAcquireInlineAuthorization(requestJson, new List<string> { enabledVersionIds[0] }, true);
                    }
                    if (!String.IsNullOrEmpty(inlineAuth))
                        responseObj["authorizeResult"] = inlineAuth;
                }
                return responseObj.ToString(Formatting.None);
            }
            catch (Exception error)
            {
                Log.ObserveException("check.and.authorize.finalize.failed", error);
                return responsedata;
            }
        }

        private string TryAcquireInlineAuthorization(JObject requestJson, List<string> enabledVersionIds, bool showFailureDialog = true)
        {
            try
            {
                string deviceId = (string)requestJson["deviceId"];
                if (String.IsNullOrWhiteSpace(deviceId)) deviceId = lastObservedDeviceId;
                string updateToken = (string)requestJson["updateToken"];
                if (String.IsNullOrWhiteSpace(updateToken)) updateToken = lastObservedUpdateToken;
                string vendor = (string)requestJson["authVendor"];
                if (String.IsNullOrWhiteSpace(vendor)) vendor = lastObservedAuthVendor;
                string ver = (string)requestJson["ver"] ?? "1";
                string imei = (string)requestJson["commonRules"]?["IMEI"];
                if (String.IsNullOrWhiteSpace(imei)) imei = lastObservedImei ?? "";
                if (String.IsNullOrEmpty(deviceId) || String.IsNullOrEmpty(updateToken) || String.IsNullOrEmpty(vendor))
                    return "";
                JArray versions = new JArray();
                foreach (string id in enabledVersionIds)
                    versions.Add(new JObject { ["versionId"] = id });
                JObject payload = new JObject
                {
                    ["IMEI"] = imei,
                    ["deviceId"] = deviceId,
                    ["updateToken"] = updateToken,
                    ["vendor"] = vendor,
                    ["ver"] = ver,
                    ["version"] = versions
                };
                string updata = payload.ToString(Formatting.None);
                byte[] requestBytes = Encoding.UTF8.GetBytes(updata);
                HttpWebRequest http = (HttpWebRequest)WebRequest.Create("https://query.hicloud.com/sp_ard_common/v1/authorize.action");
                http.Proxy = null;
                http.Method = "POST";
                http.ContentType = "application/json;charset=UTF-8";
                http.UserAgent = "HiSuite";
                http.Timeout = 10000;
                http.ReadWriteTimeout = 10000;
                http.ContentLength = requestBytes.Length;
                using (Stream requestStream = http.GetRequestStream())
                    requestStream.Write(requestBytes, 0, requestBytes.Length);
                string responseText;
                using (HttpWebResponse response = (HttpWebResponse)http.GetResponse())
                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
                    responseText = reader.ReadToEnd().Trim();
                if (responseText.Length <= 2 || (showFailureDialog && !CheckAuthentication(updata, responseText)))
                    return "";
                string dataPart = null;
                foreach (string part in responseText.Split('&'))
                    if (part.StartsWith("data=", StringComparison.Ordinal))
                        dataPart = part.Substring(5);
                if (String.IsNullOrEmpty(dataPart))
                    return "";
                JObject decoded = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(dataPart)));
                JArray approvedList = decoded["approvedVersionList"] as JArray;
                if (approvedList == null || approvedList.Count == 0)
                    return "";
                foreach (JToken item in approvedList)
                    if ((string)item["status"] != "0")
                        return "";
                Log.Event("inline.authorization.acquired", new JObject
                {
                    ["versionIds"] = new JArray(enabledVersionIds),
                    ["status"] = "0",
                    ["length"] = responseText.Length
                }.ToString(Formatting.None));
                return responseText;
            }
            catch (Exception error)
            {
                Log.ObserveException("inline.authorization.failed", error);
                return "";
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Process.Start(SetUP.OfficialHiSuite14DownloadUrl);
        }

        public bool ReplaceComponent(string rawfile, string patchedfile, bool systemadmin = false, bool ReplaceOriginalFile = true, string NewFileDirectory = null)
        {
            Process[] hisuiteopen = Process.GetProcessesByName("HiSuite");
            if(hisuiteopen.Length > 0)
            {
                hisuiteopen[0].Kill();
                Thread.Sleep(1500);
            }
            try
            {
                if(ReplaceOriginalFile)
                {
                    string targetFile = rawfile.Replace(".dll", ".bak");
                    if (File.Exists(targetFile))
                        File.Delete(targetFile);
                    File.Move(rawfile, targetFile);
                    File.Move(patchedfile, rawfile);
                }
                else
                {
                    if (File.Exists(NewFileDirectory))
                        File.Delete(NewFileDirectory);
                    File.Move(patchedfile, NewFileDirectory);
                }
                if(!systemadmin)
                {
                    MessageBox.Show("Successfully Patched!", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return true;
            }
            catch(Exception e)
            {
                if (!systemadmin)
                {
                    MessageBox.Show(e.Message);
                }
            }
            return false;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "HiSuite 14 or HTTP Component|HiSuite.exe;httpcomponent.dll|Dynamic-link library|*.dll|Executable|*.exe";
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\HiSuite";
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                Patch(dialog.FileName);
            }
        }
        public string LastPatchError { get; private set; }

        public bool Patch(string filename, bool systemadmin = false)
        {
            LastPatchError = null;
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(filename));
                string runInfo = Path.Combine(directory, "RunInfo.ini");
                if (File.Exists(runInfo) && Array.Exists(File.ReadAllLines(runInfo),
                    line => line.Trim().StartsWith("version=14.", StringComparison.OrdinalIgnoreCase)))
                {
                    string selectedFile = Path.GetFileName(filename);
                    if (!selectedFile.Equals("httpcomponent.dll", StringComparison.OrdinalIgnoreCase) &&
                        !selectedFile.Equals("HiSuite.exe", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Select HiSuite.exe or httpcomponent.dll from the HiSuite 14 directory.");
                    HiSuite14PatchStatus status = HiSuite14Patcher.Apply(directory);
                    if (!systemadmin)
                        MessageBox.Show(status.Message, "HiSuite 14", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return status.IsSupported;
                }
            }
            catch (Exception error)
            {
                LastPatchError = error.Message;
                if (!systemadmin)
                    MessageBox.Show(error.Message, "HiSuite 14 patch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            byte[] finddata = new byte[] { 0x6A, 0x00, 0x6A, 0x01, 0x51, 0xFF, 0x15, 0xC4, 0x32, 0x01, 0x10 }, replacedata = new byte[] { 0x6A, 0x00, 0x6A, 0x00, 0x51, 0xFF, 0x15, 0xC4, 0x32, 0x01, 0x10 };
            bool newHiSuite = false;
            try
            {
                string settings = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\HiSuite\RunInfo.ini";

                string hiSuiteVersionNumber = File.ReadAllText(settings);
                if (hiSuiteVersionNumber.Contains("version=8.0.1.300") || hiSuiteVersionNumber.Contains("version=8.0.1.302"))
                {
                    finddata = new byte[] { 0x6A, 0x00, 0x6A, 0x01, 0x51, 0xFF, 0x15, 0x90, 0x32, 0x01, 0x10 };
                    replacedata = new byte[] { 0x6A, 0x00, 0x6A, 0x00, 0x51, 0xFF, 0x15, 0x90, 0x32, 0x01, 0x10 };
                }
                else if (hiSuiteVersionNumber.Contains("version=10.1.0.550"))
                {
                    finddata = new byte[] { 0x6A, 0x00, 0x6A, 0x01, 0xFF, 0x35, 0x08, 0xD6, 0x02, 0x10 };
                    replacedata = new byte[] { 0x6A, 0x00, 0x6A, 0x00, 0xFF, 0x35, 0x08, 0xD6, 0x02, 0x10 };
                }
                else if (hiSuiteVersionNumber.Contains("version=11.0.0.510") || hiSuiteVersionNumber.Contains("version=11.0.0.530"))
                {
                    //6A 00 6A 01 FF 35 70 68 03 10
                    finddata = new byte[] { 0x6A, 0x00, 0x6A, 0x01, 0xFF, 0x35, 0x70, 0x68, 0x03, 0x10 };
                    //FF 35 70 68 03 10
                    replacedata = new byte[] { 0x6A, 0x00, 0x6A, 0x00, 0xFF, 0x35, 0x70, 0x68, 0x03, 0x10 };
                    newHiSuite = true;
                    //return false;
                }
				else if (hiSuiteVersionNumber.Contains("version=11.0.0.580") || hiSuiteVersionNumber.Contains("version=11.0.0.590"))
                {
                    //6A 00 6A 01 FF 35 70 78 03 10
                    finddata = new byte[] { 0x6A, 0x00, 0x6A, 0x01, 0xFF, 0x35, 0x70, 0x78, 0x03, 0x10 };
                    //FF 35 70 68 03 10
                    replacedata = new byte[] { 0x6A, 0x00, 0x6A, 0x00, 0xFF, 0x35, 0x70, 0x78, 0x03, 0x10 };
                    newHiSuite = true;
                    //return false;
                }
            }
            catch
            {

            }

            //File.AppendAllText("debugfile.txt", "Reading: " + filename + Environment.NewLine); // DEBUG
            byte[] filedata = File.ReadAllBytes(filename);

            if (PatcherReplace(finddata, replacedata, ref filedata))
            {
                if ((newHiSuite && PatcherReplace(new byte[] { 0xE8, 0x3C, 0x00, 0x00, 0x00, 0x83, 0xF8, 0x01 }, new byte[] { 0xE8, 0x3C, 0x00, 0x00, 0x00, 0x83, 0xF8, 0x05 }, ref filedata))
                    || PatcherReplace(new byte[] { 0x71, 0x75, 0x65, 0x72, 0x79, 0x2E, 0x68, 0x69, 0x63, 0x6C, 0x6F, 0x75, 0x64, 0x2E, 0x63, 0x6F, 0x6D }, new byte[] { 0x70, 0x70, 0x70, 0x70, 0x79, 0x2E, 0x68, 0x69, 0x63, 0x6C, 0x6F, 0x75, 0x64, 0x2E, 0x63, 0x6F, 0x6D }, ref filedata))
                {
                    string tempfile = Path.GetTempPath() + "httpcomponent.dll";

                    //File.AppendAllText("debugfile.txt", "Writing: " + tempfile + Environment.NewLine); // DEBUG
                    File.WriteAllBytes(tempfile, filedata);
                    if(!systemadmin)
                    {
                        Process startprogram = new Process();
                        startprogram.StartInfo.FileName = Process.GetCurrentProcess().MainModule.FileName;
                        startprogram.StartInfo.Arguments = "\"" + filename + "\" \"" + tempfile + "\"";
                        startprogram.StartInfo.Verb = "runas";
                        try
                        {
                            startprogram.Start();
                        }
                        catch
                        {

                        }
                    }
                    else
                    {
                        if (newHiSuite)
                        {
                            string HISuiteDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + @"\HiSuite\";
                            //File.AppendAllText("debugfile.txt", "Replacing: " + tempfile + " with " + HISuiteDir + Environment.NewLine); // DEBUG
                            bool succeded1 = ReplaceComponent(null, tempfile, true, false, HISuiteDir + "httpcomponenb.dll");

                            //File.AppendAllText("debugfile.txt", "Reading: " + HISuiteDir + "HiSuite.exe" + Environment.NewLine); // DEBUG
                            filedata = File.ReadAllBytes(HISuiteDir + "HiSuite.exe");

                            finddata = new byte[] { 0x68, 0x74, 0x74, 0x70, 0x63, 0x6f, 0x6d, 0x70, 0x6f, 0x6e, 0x65, 0x6e, 0x74, 0x2e, 0x64, 0x6c, 0x6c };
                            replacedata = new byte[] { 0x68, 0x74, 0x74, 0x70, 0x63, 0x6f, 0x6d, 0x70, 0x6f, 0x6e, 0x65, 0x6e, 0x62, 0x2e, 0x64, 0x6c, 0x6c };

                            byte[] finddata2 = new byte[] { 0x43, 0x6f, 0x6d, 0x6d, 0x42, 0x61, 0x73, 0x65, 0x2e, 0x64, 0x6c, 0x6c };
                            byte[] replacedata2 = new byte[] { 0x43, 0x6f, 0x6d, 0x6d, 0x42, 0x61, 0x7a, 0x65, 0x2e, 0x64, 0x6c, 0x6c };


                            if (PatcherReplace(finddata, replacedata, ref filedata) && PatcherReplace(finddata2, replacedata2, ref filedata))
                            {
                                tempfile = Path.GetTempPath() + "HiSuite 11.exe";
                                //File.AppendAllText("debugfile.txt", "Writing: " + tempfile + Environment.NewLine); // DEBUG
                                File.WriteAllBytes(tempfile, filedata);
                                //File.AppendAllText("debugfile.txt", "Replacing: " + tempfile + " with " + HISuiteDir + "HiSuite 11.exe" + Environment.NewLine); // DEBUG
                                bool succeded2 = ReplaceComponent(null, tempfile, true, false, HISuiteDir + "HiSuite 11.exe");
                                //File.AppendAllText("debugfile.txt", "Reading: " + HISuiteDir + "CommBase.dll" + Environment.NewLine); // DEBUG
                                filedata = File.ReadAllBytes(HISuiteDir + "CommBase.dll");

                                if (PatcherReplace(finddata, replacedata, ref filedata))
                                {
                                    tempfile = Path.GetTempPath() + "CommBaze.dll";
                                    //File.AppendAllText("debugfile.txt", "Writing: " + tempfile + Environment.NewLine); // DEBUG
                                    File.WriteAllBytes(tempfile, filedata);
                                    //File.AppendAllText("debugfile.txt", "Replacing: " + tempfile + " with " + HISuiteDir + Environment.NewLine); // DEBUG
                                    bool succeded3 = ReplaceComponent(null, tempfile, true, false, HISuiteDir + "CommBaze.dll");
                                    if(succeded1 && succeded2 && succeded3)
                                    {
                                        //File.AppendAllText("debugfile.txt", "Creating shortcut: " + HISuiteDir + @"HiSuite 11.exe" + Environment.NewLine); // DEBUG
                                        CreateShortcut(HISuiteDir + @"HiSuite 11.exe");
                                    }
                                    return (succeded1 && succeded2 && succeded3);
                                }
                                else
                                {
                                    return false;
                                }
                            }
                            else
                            {
                                return false;
                            }
                        }
                        else
                        {
                            return ReplaceComponent(filename, tempfile, true);
                        }
                    }
                    return true;
                }
                else
                {
                    if(!systemadmin)
                        MessageBox.Show("Some errors occured in the patching process!", "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
            else
            {
                if(FindArray(filedata, replacedata) == -1)
                {
                    if (!systemadmin)
                        MessageBox.Show("Could not patch this version of HiSuite.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
                else
                {
                    if (!systemadmin)
                        MessageBox.Show("File is already patched.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                }
            }
        }
        private bool PatcherReplace(byte[] data, byte[] replacewith, ref byte[] basedata)
        {
            int dataindex = FindArray(basedata, data);
            if(dataindex == -1)
            {
                return false;
            }
            else
            {
                for(int i = 0, j = replacewith.Length; i < j; i++)
                {
                    basedata[dataindex + i] = replacewith[i];
                }
                return true;
            }
        }
        private int FindArray(byte[] basedata, byte[] searchdata)
        {
            int b = searchdata.Length;
            for (int i = 0, j = basedata.Length; i < j; i++)
            {
                if(b > (j - i))
                {
                    break;
                }
                bool succeed = true;
                for(int a = 0; a < b; a++)
                {
                    if(basedata[i + a] != searchdata[a])
                    {
                        succeed = false;
                        break;
                    }
                }
                if(succeed)
                {
                    return i;
                }
            }
            return -1;
        }
        private void button4_Click(object sender, EventArgs e)
        {
            if(!firmFinder.Visible)
            {
                firmFinder.Show(this);
                firmFinder.BringToFront();
                firmFinder.Focus();
            }          
        }
        private bool CopyingBase = false, CopyingCust = false, CopyingPreload = false;

        private void button5_Click(object sender, EventArgs e)
        {
            Process.Start("https://www.paypal.com/cgi-bin/webscr?cmd=_donations&business=fullclip39@gmail.com&item_name=HISuite+Proxy+Support&no_shipping=1&lc=US");
        }

        private void button6_Click(object sender, EventArgs e)
        {
            Process startprogram = new Process();
            startprogram.StartInfo.FileName = Process.GetCurrentProcess().MainModule.FileName;
            startprogram.StartInfo.Arguments = "\"SETUP\"";
            startprogram.StartInfo.Verb = "runas";
            try
            {
                startprogram.Start();
            }
            catch
            {

            }
        }

        private void CopyFile(string romname, string filename, string packagename, int filekind)
        {
            if (filekind == 0)
            {
                if (CopyingBase)
                    return;
                CopyingBase = true;
            }
            else if (filekind == 1)
            {
                if (CopyingCust)
                    return;
                CopyingCust = true;
            }
            else if (filekind == 2)
            {
                if (CopyingPreload)
                    return;
                CopyingPreload = true;
            }
            Progress progress = new Progress("Copying File For " + romname);
            new Thread(() =>
            {
                
                bool finished = false, formLoaded = false;
                progress.Load += delegate
                {
                    formLoaded = true;
                };
                Thread CopyThread = new Thread(() =>
                {
                    try
                    {
                        while (!formLoaded)
                        {
                            Thread.Sleep(400);
                        }

                        CopyItPlease(filekind, progress, romname, filename, packagename);
                        finished = true;
                    }
                    catch
                    {

                    }
                    Thread.Sleep(400);
                    progress.Close();
                });
                progress.FormClosing += delegate
                {
                    if (filekind == 0)
                    {
                        CopyingBase = false;
                    }
                    else if (filekind == 1)
                    {
                        CopyingCust = false;
                    }
                    else if (filekind == 2)
                    {
                        CopyingPreload = false;
                    }
                    if (!finished)
                    {
                        CopyThread.Abort();
                    }
                };
                CopyThread.Start();
                Invoke(new Action(() =>
                {
                    progress.Show();
                }));
            }).Start();
        }

        private string GetFileMD5CheckSum(Stream fileStream)
        {
            string MD5Result = null;
            fileStream.Position = 0;
            using (MD5 MD5 = MD5.Create())
            {
                MD5Result = BitConverter.ToString(MD5.ComputeHash(fileStream)).Replace("-", "").ToUpper();
            }
            return MD5Result;
        }

        private string GetFileSHA256CheckSum(Stream fileStream)
        {
            string SHA256Result = null;
            fileStream.Position = 0;
            using (SHA256 sha256 = SHA256.Create())
            {
                SHA256Result = BitConverter.ToString(sha256.ComputeHash(fileStream)).Replace("-", "").ToUpper();
            }
            return SHA256Result;
        }

        private void custpkgSet_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*";
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                custPKGData = LoadPackage(dialog.FileName);
                if (custPKGData != null)
                {
                    custpkgName.Text = custPKGData.PackageName;
                    textBox6.Enabled = false;
                    textBox6.Text = custpkgName.Text;

                    CopyFile(custpkgName.Text, dialog.FileName, Path.GetFileName(dialog.FileName), 1);
                }
                else
                {
                    textBox6.Enabled = true;
                }
            }
            else
            {
                custpkgName.Text = "Not Set";
                textBox6.Enabled = true;
                custPKGData = null;
            }
        }

        private void preloadpkgSet_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*";
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                preloadPGKData = LoadPackage(dialog.FileName);
                if (preloadPGKData != null)
                {
                    preloadpkgName.Text = preloadPGKData.PackageName;
                    textBox5.Enabled = false;
                    textBox5.Text = preloadpkgName.Text;

                    CopyFile(preloadpkgName.Text, dialog.FileName, Path.GetFileName(dialog.FileName), 2);
                }
                else
                {
                    textBox5.Enabled = true;
                }
            }
            else
            {
                preloadpkgName.Text = "Not Set";
                textBox5.Enabled = true;
                preloadPGKData = null;
            }
        }

        private void CreateShortcut(string Source)
        {
            object shDesktop = (object)"Desktop";
            IWshRuntimeLibrary.WshShell shell = new IWshRuntimeLibrary.WshShell();
            string shortcutAddress = (string)shell.SpecialFolders.Item(ref shDesktop) + @"\HISuite 11.lnk";
            IWshRuntimeLibrary.IWshShortcut shortcut = (IWshRuntimeLibrary.IWshShortcut)shell.CreateShortcut(shortcutAddress);
            shortcut.IconLocation = AppDomain.CurrentDomain.BaseDirectory + "icon.ico";
            shortcut.Description = "Patched HISuite 11";
            shortcut.TargetPath = Source;
            shortcut.Save();
        }
        private PackageData LoadPackage(string filename)
        {
            PackageData packageData = new PackageData(null, null, null, null, null);
            try
            {
                using (Stream fileStream = File.OpenRead(filename))
                {
                    using (ZipArchive zipInputStream = new ZipArchive(fileStream))
                    {
                        foreach (ZipArchiveEntry entry in zipInputStream.Entries)
                        {
                            if (entry.Name.Contains("VERSION.mbn"))
                            {
                                int ReadSize = (int)entry.Length;
                                if (ReadSize == -1)
                                    ReadSize = 1024;
                                byte[] readSize = new byte[ReadSize];
                                Stream entryStream = entry.Open();
                                int ReadBytes = entryStream.Read(readSize, 0, readSize.Length);
                                entryStream.Close();
                                entryStream.Dispose();
                                if (ReadBytes > 0)
                                {
                                    string PackageName = Encoding.UTF8.GetString(readSize, 0, ReadBytes);
                                    PackageName = PackageName.Trim();
                                    int where = PackageName.IndexOf('\r'), where2 = PackageName.IndexOf('\n');
                                    if (where != -1 && (where < where2 || where2 == -1))
                                    {
                                        PackageName = PackageName.Substring(0, where);
                                    }
                                    else if (where2 != -1)
                                    {
                                        PackageName = PackageName.Substring(0, where2);
                                    }
                                    packageData.PackageName = PackageName;
                                }
                                break;
                            }
                        }

                        if (packageData.PackageName != null)
                        {
                            packageData.PackageFile = Path.GetFileName(filename);

                            packageData.PackageSize = fileStream.Length.ToString();

                            packageData.PackageSha256 = GetFileSHA256CheckSum(fileStream);

                            packageData.PackageMD5 = GetFileMD5CheckSum(fileStream);
                            return packageData;
                        }
                    }
                }
            }
            catch(Exception e)
            {
                MessageBox.Show(e.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return null;
        }
        private void basepkgSet_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "ZIP Files (*.zip)|*.zip|All Files (*.*)|*.*";
            if(dialog.ShowDialog() == DialogResult.OK)
            {
                basePKGData = LoadPackage(dialog.FileName);
                if (basePKGData != null)
                {
                    basepkgName.Text = basePKGData.PackageName;
                    textBox2.Enabled = false;
                    textBox2.Text = basepkgName.Text;

                    CopyFile(basepkgName.Text, dialog.FileName, Path.GetFileName(dialog.FileName), 0);
                }
                else
                {
                    textBox2.Enabled = true;
                }
            }
            else
            {
                basepkgName.Text = "Not Set";
                basePKGData = null;
                textBox2.Enabled = true;
            }
        }

        private void CopyItPlease(int FileKind, Progress progress, string romname, string filename, string packagename)
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\HiSuite\ROM\" + romname + @"\full\" + packagename;
                
                if(File.Exists(dir))
                {
                    long victimlen = new FileInfo(dir).Length, mainfilelen = new FileInfo(filename).Length;
                    if(victimlen != mainfilelen)
                    {
                        File.Delete(dir);
                    }
                    else
                    {
                        progress.SetProgress(100);
                        return;
                    }
                }
                string Directoryname = Path.GetDirectoryName(dir);
                if(!Directory.Exists(Directoryname))
                {
                    Directory.CreateDirectory(Directoryname);
                }
                if(FileKind > 0)
                {
                    if(FileKind == 1)
                    {
                        while(CopyingBase || CopyingPreload)
                        {
                            Thread.Sleep(1500);
                        }
                    }
                    else
                    {
                        while (CopyingBase || CopyingCust)
                        {
                            Thread.Sleep(1500);
                        }
                    }
                }
                using (FileStream stream = File.OpenRead(filename))
                {
                    long totallen = stream.Length;
                    using(FileStream write = File.OpenWrite(dir))
                    {
                        long writtenlen = 0;
                        int readdata = 4096;
                        byte[] data = new byte[readdata];
                        while((readdata = stream.Read(data, 0, data.Length)) > 0)
                        {
                            write.Write(data, 0, readdata);
                            writtenlen += readdata;
                            int percentage = (int)((writtenlen * 100) / totallen);
                            if((percentage % 5) == 0)
                            {

                                progress.SetProgress(percentage);

                            }
                        }
                    }
                }
            }
            catch(Exception e)
            {
                if(!e.Message.StartsWith("Thread was being"))
                MessageBox.Show(e.Message, "ERROR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private bool CheckAuthentication(string phonedata, string response)
        {
            try
            {
                int where = response.IndexOf("data=");
                if (where != -1)
                {
                    where += 5;
                    int finish = response.IndexOf('&', where);
                    string base64 = Encoding.UTF8.GetString(Convert.FromBase64String(response.Substring(where, finish - where)));
                    if (base64.Contains("\"status\":\"1\"") || base64.Contains("\"status\":\"2\""))
                    {
                        new Thread(() =>
                        {
                            MessageBox.Show("Apparently Firm(s) is/are not approved for installation and proccess will most likely fail in phone.\r\n\r\nYou might want to consider cancelling it to avoid time waste.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }).Start();
                        return true;
                    }
                    if(base64.Contains("\"status\":\"0\""))
                    {
                        where = phonedata.IndexOf("vendor");
                        if (where != -1)
                        {
                            where += 7;
                            where = phonedata.IndexOf('"', where);
                            if (where != -1)
                            {
                                where++;
                                finish = phonedata.IndexOf('-', where) + 1;
                                string phonemodel = phonedata.Substring(where, finish - where).ToUpper();
                                where = 0;
                                while ((where = base64.IndexOf("versionNumber", where)) != -1)
                                {
                                    where += 14;
                                    where = base64.IndexOf('"', where);
                                    where++;
                                    finish = base64.IndexOf('"', where);
                                    string responsemodel = base64.Substring(where, finish - where).ToUpper();
                                    if(!responsemodel.Contains("PATCH") && !responsemodel.Contains(phonemodel))
                                    {
                                        string showstr = "Your phone model: " + phonemodel + "\r\n";
                                        showstr += "Installing Firm Of: " + responsemodel + "\r\n\r\n";
                                        showstr += "Do you want to proceed? ( Might soft-brick your phone )";
                                        if(MessageBox.Show(showstr, "Firm Mis-Match", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation) == DialogResult.No)
                                        {
                                            return false;
                                        }
                                    }
                                }
                                return true;
                            }
                            else
                            {
                                return true;
                            }
                        }
                        else
                        {
                            return true;
                        }
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    return false;
                }
            }
            catch
            {
                return true;
            }
        }
    }
}
