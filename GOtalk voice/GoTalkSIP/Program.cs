using System;
using System.Net;
using System.Threading.Tasks;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorcery.Media;
using GenerativeAI;
using GenerativeAI.Live;
using static GenerativeAI.VertexAIModels;
using System.Text;

const string sipUser = "052919748";
const string sipPassword = "gotalk21";
const string sipServer = "sip.megafon.bg";

const string geminiKey = "AIzaSyDMZnNVJZngw_wt5NJjNbCQlT7KWmf62G4";

//gemini initializtaion
MultiModalLiveClient gemini = new MultiModalLiveClient(
    new GoogleAIPlatformAdapter(geminiKey, apiVersion: "v1beta"),
    modelName: "models/gemini-2.0-flash-live-001",
    systemInstruction:""
);

// log gemini response to console
gemini.TextChunkReceived += (_, e) =>
{
    if (!string.IsNullOrEmpty(e.Text))
        Console.Write(e.Text);
};

Console.WriteLine("Gemini connecting");
await gemini.ConnectAsync(autoSendSetup: true);
Console.WriteLine("Gemini connected :)");

SIPTransport sipTransport = new SIPTransport();
sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 5060))); //listeing on udp 5060

SIPRegistrationUserAgent regUa = new SIPRegistrationUserAgent(sipTransport, sipUser, sipPassword, sipServer, expiry: 300);
TaskCompletionSource<bool> regTcs = new TaskCompletionSource<bool>();

//logging for testing purposes

regUa.RegistrationSuccessful += (uri, resp) =>
{
    Console.WriteLine($"Registerd. {uri} Resp: {resp.StatusCode} {resp.ReasonPhrase}");
    if (!regTcs.Task.IsCompleted) regTcs.TrySetResult(true);
};

regUa.RegistrationFailed += (uri, resp, err) =>
{
    Console.WriteLine($"Registration failed - {uri}: {resp.StatusCode} {resp.ReasonPhrase}  Msg: {err}");
    if (!regTcs.Task.IsCompleted) regTcs.TrySetResult(false);
};

Console.WriteLine("Registering SIP...");
regUa.Start();

SIPUserAgent userAgent = new SIPUserAgent(sipTransport, null, true);

//handle sip invite
userAgent.OnIncomingCall += async (ua, req) =>
{
    Console.WriteLine($"Yahooo! Incoming call: {req.Header.From?.FromURI}");

    //TODO
    //get audio from phone, send to gemini, log gemini responses to console, connect elevenlabs and tts responses and play them
    SIPServerUserAgent uas = ua.AcceptCall(req);
    VoIPMediaSession media = new VoIPMediaSession { AcceptRtpFromAny = true };

    media.OnRtpPacketReceived += (remoteEP, kind, pkt) =>
    {
        Console.WriteLine("poluchavam audioooo");
    };

    await ua.Answer(uas, media);
    Console.WriteLine("Call answered.");

    ua.OnCallHungup += _ =>
    {
        Console.WriteLine("Call hangup. Bye!");
        try { media.Close("Hangup"); } catch { }
    };

};

Console.WriteLine("Waiting for calls... Call 052919748");
await Task.Delay(-1);





