(() => {
  const logEl = document.getElementById('log');
  const player = document.getElementById('player');
  const talkBtn = document.getElementById('talkBtn');
  const stopBtn = document.getElementById('stopBtn');
  const connectBtn = document.getElementById('connectBtn');
  const statusEl = document.getElementById('status');
  const wsUrlEl = document.getElementById('wsUrl');

  let ws, ac, micStream, workletNode, recording = false;

  //adds text to the "messagebox"
  function log(t)
  { 
    logEl.textContent += t + "\n"; 
    logEl.scrollTop = logEl.scrollHeight; 
  }

  //sets disconnected/connected as text
  function setStatus(s)
  { 
    statusEl.textContent = s; 
  }

  //connect/disconnect ws
  connectBtn.onclick = () => {

    //if ws is connected - close it
    if (ws && ws.readyState === WebSocket.OPEN) 
      {
         ws.close(); 
        return; 
      }

    ws = new WebSocket(wsUrlEl.value);
    ws.binaryType = "arraybuffer";

    ws.onopen = () => setStatus('connected'); 
    ws.onclose = () => { 
      setStatus('disconnected'); 
      if (recording) stopCapture(); 
    };

    ws.onerror = () => log('Проблем със свързването към ws.');

    ws.onmessage = async (ev) => {
      try {
        const msg = JSON.parse(ev.data);
        if (msg.text) {
        log(`Миро: ${msg.text}`);

        const audioBlob = await getTTS(ws, msg.text);
        player.src = URL.createObjectURL(audioBlob);
        player.play().catch(() => {});
    }
      } catch {}
};
  };

  //starts/stops recording mic
  talkBtn.onclick = async () => {
    if (!ws || ws.readyState !== WebSocket.OPEN) 
      { 
        log('WS не е свързан :('); 
        return; 
      }

    if (!recording) 
      { 
        await startCapture(); 
      }
    else 
      { 
        await stopCapture(); 
      }
  };

  //stop recording mic
  stopBtn.onclick = () => { 
    if (recording) stopCapture(); 
  };

  //captures sound from mic in 16kHz and sends to backend as raw PCM
  async function startCapture() {
    recording = true; 
    talkBtn.classList.add('on'); 
    talkBtn.textContent = "Спри";
    //audio context with 16kHz sample rate needed for gemini
    ac = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });

    micStream = await navigator.mediaDevices.getUserMedia(
      { 
        audio: { channelCount: 1, noiseSuppression: true, echoCancellation: true } 
      });

    const src = ac.createMediaStreamSource(micStream);

    await ac.audioWorklet.addModule('./js/worklet.js');
    workletNode = new AudioWorkletNode(ac, 'pcm-worklet');

    workletNode.port.onmessage = (e) => {
      if (!recording) return;

      ws.send(new Uint8Array(e.data.buffer)); //raw PCM sent to the backend
    };

    src.connect(workletNode).connect(ac.destination);

    log('🎙️ запис...');
  };

  async function stopCapture() {
    recording = false; 
    talkBtn.classList.remove('on'); 
    talkBtn.textContent = "Говори";
    try { 
      ws && ws.readyState===WebSocket.OPEN && ws.send(JSON.stringify({ type: 'audioStreamEnd' })); 
    } 
    catch {}

    try { 
      workletNode && workletNode.disconnect(); 
    } 
    catch {}

    try { 
      micStream && micStream.getTracks().forEach(t=>t.stop()); 
    } 
    catch {}

    try { 
      ac && ac.close(); 
    }
    catch {}

    log('Спря да говориш.');
  };

  //gets the voice message
  function getTTS(ws, text, { timeoutMs = 15000, mime = 'audio/mpeg' } = {}) {

    return new Promise((resolve, reject) => {

      //arraybuffer = binary = audio
      const onMessage = (ev) => {
        if (ev.data instanceof ArrayBuffer) {
          cleanup();
          resolve(new Blob([ev.data], { type: mime })); //returns audio blob with resolve
        }
      };

      //no audio for 15sec
      const to = setTimeout(() => {
        cleanup();
        reject(new Error('TTS timeout')); //return error with reject
      }, timeoutMs);

      //stops the timer and removes listener
      const cleanup = () => {
        clearTimeout(to);
        ws.removeEventListener('message', onMessage);
      };

      ws.addEventListener('message', onMessage);
      ws.send(JSON.stringify({ type: 'tts', text }));
    });

  }
})();