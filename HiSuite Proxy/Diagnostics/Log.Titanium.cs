using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy;

namespace ProxyDiagnostics
{
    public static class TitaniumHooks
    {
        private static readonly ConditionalWeakTable<ProxyServer, object> Registered = new ConditionalWeakTable<ProxyServer, object>();

        public static void RegisterResponseObserver(object form)
        {
            try
            {
                FieldInfo field = form.GetType().GetField("proxyserver", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                ProxyServer server = field == null ? null : field.GetValue(form) as ProxyServer;
                if (server == null) return;
                lock (Registered)
                {
                    object marker;
                    if (Registered.TryGetValue(server, out marker)) return;
                    Registered.Add(server, new object());
                    server.BeforeResponse += delegate(object sender, SessionEventArgs session) { return CaptureResponse(session); };
                }
                Log.Event("diagnostics.response.observer.registered", "API responses are captured; firmware ZIP bodies remain streamed.");
            }
            catch (Exception error) { Log.ObserveException("diagnostics.response.observer.failed", error); }
        }

        private static async Task CaptureResponse(SessionEventArgs session)
        {
            try
            {
                Log.Checkpoint(session, "response.observer.headers");
                if (!Query(session)) return;
                string body = await session.GetResponseBodyAsString(CancellationToken.None).ConfigureAwait(false);
                Log.ObserveResponse("response.observed.to.hisuite", Url(session), body);
            }
            catch (Exception error) { Log.ObserveException("diagnostics.response.observer.read.failed", error); }
        }

        private static bool Query(SessionEventArgs session)
        {
            Uri uri;
            return Uri.TryCreate(Url(session), UriKind.Absolute, out uri) && uri.Host.Equals("query.hicloud.com", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task AfterRequest(Task original, SessionEventArgs session)
        {
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { Log.ObserveException("request.handler.failed", error); throw; }
            Log.Checkpoint(session, "request.handler.complete");
            if (!Query(session) || !session.HttpClient.Request.HasBody) return;
            try
            {
                string body = await session.GetRequestBodyAsString(CancellationToken.None).ConfigureAwait(false);
                Log.ObserveRequest("request.final.before.forward", Url(session), body);
            }
            catch (Exception error) { Log.ObserveException("diagnostics.request.read.failed", error); }
        }

        public static async Task AfterResponse(Task original, SessionEventArgs session)
        {
            try { await original.ConfigureAwait(false); }
            catch (Exception error) { Log.ObserveException("response.handler.failed", error); throw; }
            Log.Checkpoint(session, "response.handler.complete");
            // Read only small API responses here; firmware archives remain streamed.
            if (!Query(session)) return;
            try
            {
                string body = await session.GetResponseBodyAsString(CancellationToken.None).ConfigureAwait(false);
                Log.ObserveResponse("response.final.to.hisuite", Url(session), body);
            }
            catch (Exception error) { Log.ObserveException("diagnostics.response.read.failed", error); }
        }

        private static string Url(SessionEventArgs session)
        {
            try { return session.HttpClient.Request.Url; }
            catch { return null; }
        }

        public static async Task<string> GetRequestBodyAsString(SessionEventArgs session, CancellationToken cancellation)
        {
            Log.Checkpoint(session, "request.body.read");
            try
            {
                string body = await session.GetRequestBodyAsString(cancellation).ConfigureAwait(false);
                return Log.ObserveRequest("incoming.body", Url(session), body);
            }
            catch (Exception error) { Log.ObserveException("request.body.read.error", error); throw; }
        }

        public static async Task<string> GetResponseBodyAsString(SessionEventArgs session, CancellationToken cancellation)
        {
            Log.Checkpoint(session, "response.body.read");
            try
            {
                string body = await session.GetResponseBodyAsString(cancellation).ConfigureAwait(false);
                return Log.ObserveResponse("received.body", Url(session), body);
            }
            catch (Exception error) { Log.ObserveException("response.body.read.error", error); throw; }
        }

        public static void SetRequestBodyString(SessionEventArgs session, string body)
        {
            Log.Checkpoint(session, "request.replacement.commit");
            session.SetRequestBodyString(Log.ObserveRequest("rewritten.body.committed", Url(session), body));
        }

        public static void Ok(SessionEventArgs session, string body)
        {
            Log.Checkpoint(session, "response.override.commit");
            session.Ok(Log.ObserveResponse("response.sent.to.hisuite", Url(session), body));
        }

        public static void Ok(SessionEventArgs session, string body, IDictionary<string, HttpHeader> headers, bool closeServerConnection = false)
        {
            Log.Checkpoint(session, "response.override.commit");
            session.Ok(Log.ObserveResponse("response.sent.to.hisuite", Url(session), body), headers, closeServerConnection);
        }

        public static void Ok(SessionEventArgs session, string body, IEnumerable<HttpHeader> headers, bool closeServerConnection)
        {
            Log.Checkpoint(session, "response.override.commit");
            session.Ok(Log.ObserveResponse("response.sent.to.hisuite", Url(session), body), headers, closeServerConnection);
        }
    }
}
