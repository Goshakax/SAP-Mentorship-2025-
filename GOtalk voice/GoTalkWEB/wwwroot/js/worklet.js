class PCMWorklet extends AudioWorkletProcessor {
  constructor(){ 
    super(); 
    this.buf = new Int16Array(0); 
    this.batch = 2048; 
    }
  
  process(inputs){
    const ch = inputs[0][0]; 
    if(!ch){
      return true;
    }
    
    const chunk = new Int16Array(ch.length);
    for (let i=0; i<ch.length; i++){ 
        const s=Math.max(-1,Math.min(1,ch[i])); chunk[i]=(s<0?s*0x8000:s*0x7FFF)|0; 
    }

    const out = new Int16Array(this.buf.length + chunk.length); 
    out.set(this.buf,0); 
    out.set(chunk,this.buf.length); 
    this.buf = out;
    
    while (this.buf.length >= this.batch){ 

        this.port.postMessage(this.buf.slice(0,this.batch)); 
        this.buf = this.buf.slice(this.batch); 
    }
    return true;
  }
}
registerProcessor('pcm-worklet', PCMWorklet);