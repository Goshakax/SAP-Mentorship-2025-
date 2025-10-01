using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GenerativeAI;
using GenerativeAI.Live;
using GenerativeAI.Types;


var build = WebApplication.CreateBuilder(args);
var app = build.Build();

app.UseWebSockets
    (new WebSocketOptions
    { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.UseDefaultFiles();
app.UseStaticFiles();

app.Map("/ws", async ctx =>
{
    //ensures handshake is a ws 
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Expected WebSocket");
        return;
    }


    string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsync("Missing GEMINI_API_KEY");
        return;
    }

    //Gemini initialization 
    MultiModalLiveClient gemini = new MultiModalLiveClient(
        new GoogleAIPlatformAdapter(apiKey, apiVersion: "v1beta"),
        modelName: "models/gemini-2.0-flash-live-001",
        systemInstruction: "Ти си Миро Лудака. Пасивноагресивен AI модел, който винаги е готов да ти помогне, освен ако не го изнервиш."
    );

    using WebSocket browser = await ctx.WebSockets.AcceptWebSocketAsync();
    CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);


    StringBuilder sb = new();
    System.Timers.Timer flushTimer = new(400) { AutoReset = false };

    //append text chunks to sb and restart timer
    gemini.TextChunkReceived += (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            sb.Append(e.Text);
            flushTimer.Stop();
            flushTimer.Start();
        }
    };

    flushTimer.Elapsed += async (_, __) => await FlushAsync(); //timer ends = gemini stopped generating a response

    try
    {
        await gemini.ConnectAsync(autoSendSetup: true, cancellationToken: cts.Token);

        byte[] buffer = new byte[8192]; //buffer for storing from ws

        while (browser.State == System.Net.WebSockets.WebSocketState.Open && !cts.IsCancellationRequested)
        {
            WebSocketReceiveResult res = await browser.ReceiveAsync(buffer, cts.Token); //result contains msgtype, count and endofmsg

            if (res.MessageType == WebSocketMessageType.Close)
                break;

            //handle 16kHz raw PCM from the browser and send it to gemini
            if (res.MessageType == WebSocketMessageType.Binary && res.Count > 0)
            {
                //copying received bytes into a exact size arr, not 8192 like the buffer
                byte[] pcm = new byte[res.Count];
                Array.Copy(buffer, pcm, res.Count);

                await gemini.SendAudioAsync(
                    pcm,
                    mimeType: "audio/pcm;rate=16000",
                    cancellationToken: cts.Token);
            }
            else if (res.MessageType == WebSocketMessageType.Text) //handle anything other than audio - tts/audiostreamend
            {
                StringBuilder resMessage = new StringBuilder();
                resMessage.Append(Encoding.UTF8.GetString(buffer, 0, res.Count)); //again, copy filled space from buffer to an exact size variable

                while (!res.EndOfMessage)//incase message is parted
                {
                    res = await browser.ReceiveAsync(buffer, cts.Token);

                    if (res.MessageType != WebSocketMessageType.Text) break;

                    resMessage.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                }

                await HandleControlMessage(resMessage.ToString());
            }
        }
    }
    catch //close ws when exception
    {
        if (browser.State == System.Net.WebSockets.WebSocketState.Open)
            await browser.CloseAsync(WebSocketCloseStatus.InternalServerError, "rip server", cts.Token);
    }
    finally //close everything cleanup
    {
        cts.Cancel();

        try
        {
            await gemini.DisconnectAsync();
        }
        catch { }

        try
        {
            if (browser.State == System.Net.WebSockets.WebSocketState.Open)
                await browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch { }
    }

    static async Task SendTTSAsync(string text, WebSocket socket, CancellationToken ct)
    {

        string? elevenKey = Environment.GetEnvironmentVariable("ELEVEN_API_KEY");
        string? voiceId = Environment.GetEnvironmentVariable("ELEVEN_VOICE_ID");
        if (string.IsNullOrWhiteSpace(elevenKey) || string.IsNullOrWhiteSpace(voiceId))
        {
            if (socket.State == WebSocketState.Open)
            {
                var msg = Encoding.UTF8.GetBytes("Invalid elevenlabs key/voice :(");
                await socket.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            if (socket.State == WebSocketState.Open)
            {
                var msg = Encoding.UTF8.GetBytes("Text to tts is empty");
                await socket.SendAsync(msg, WebSocketMessageType.Text, true, ct);
            }
            return;
        }

        using HttpClient http = new HttpClient();
        http.DefaultRequestHeaders.Add("xi-api-key", elevenKey);

        //initialize elevenlabs model
        var body = new
        {
            model = "eleven_multilingual_v2",
            text = text,
            output_format = "mp3_44100_64",
            voice_settings = new
            {
                stability = 0.3f,
                similarity_boost = 1f,
                speaking_rate = 1.6f
            }
        };

        //post req to elevenlabs
        using HttpRequestMessage req = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            if (socket.State == WebSocketState.Open)
            {
                byte[] bytes = Encoding.UTF8.GetBytes("TTS Failed ;(");
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            return;
        }

        await using Stream audio = await res.Content.ReadAsStreamAsync(ct);

        await PingPongStream(audio, socket, ct);
    }

    async Task HandleControlMessage(string msg)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(msg);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("type", out var t))
            {
                string type = t.GetString();

                if (type == "audioStreamEnd") 
                {
                    flushTimer.Stop();
                    await FlushAsync();
                }
                else if (type == "tts")
                {
                    string text = root.TryGetProperty("text", out JsonElement te) ? te.GetString() ?? "" : ""; 
                    _ = Task.Run(() => SendTTSAsync(text ?? string.Empty, browser, cts.Token), cts.Token);
                }
            }
        }
        catch
        {
        }
    }

    //get the final response, ensure it is not empty and send it to the client
    async Task FlushAsync()
    {
        string finalRes = sb.ToString().Trim();
        sb.Clear();
        if (!string.IsNullOrEmpty(finalRes) && browser.State == System.Net.WebSockets.WebSocketState.Open)
        {
            string json = $"{{\"text\":\"{finalRes}\"}}";
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            await browser.SendAsync(buffer, WebSocketMessageType.Text, true, ctx.RequestAborted);
        }
    }
});

app.Run();


// audio from stream to ws using ping pong buffering.
// essentialy - while bufA is being sent, bufB is being filled then both are swapped
static async Task PingPongStream(Stream audio, WebSocket socket, CancellationToken ct)
{
    byte[] bufA = new byte[32 * 1024];
    byte[] bufB = new byte[32 * 1024];

    int readA = await audio.ReadAsync(bufA, 0, bufA.Length, ct);
    if (readA <= 0) return;

    while (socket.State == WebSocketState.Open)
    {
        int readB = await audio.ReadAsync(bufB, 0, bufB.Length, ct);
        bool isLast = readB <= 0;

        await socket.SendAsync(new ArraySegment<byte>(bufA, 0, readA),
                               WebSocketMessageType.Binary,
                               isLast,
                               ct);

        if (isLast) break;

        (bufA, bufB) = (bufB, bufA);
        readA = readB;
    }
}