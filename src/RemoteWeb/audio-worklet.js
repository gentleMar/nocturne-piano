// A bounded 48 kHz stereo ring; old samples are dropped instead of accumulating delay.
class PianoStream extends AudioWorkletProcessor {
  constructor() {
    super(); this.ring = new Float32Array(32768); this.write = 0; this.read = 0;
    this.started = false; this.enabled = true;
    this.port.onmessage = ({data}) => {
      if (data.reset) { this.read = this.write; this.started = false; this.enabled = data.enabled; return; }
      if (!this.enabled) return;
      const pcm = new Int16Array(data);
      for (let i = 0; i < pcm.length; i++) this.ring[(this.write + i) % this.ring.length] = pcm[i] / 32768;
      this.write += pcm.length;
      if (this.write - this.read > 9600) this.read = this.write - 2880; // cap 100 ms; recover to 30 ms
    };
  }
  process(inputs, outputs) {
    const channels = outputs[0];
    if (!this.enabled) return true;
    if (!this.started && this.write - this.read >= 2880) this.started = true;
    if (!this.started) return true;
    const step = 48000 / sampleRate;
    for (let i = 0; i < channels[0].length; i++) {
      if (this.write - this.read < 4) { this.started = false; break; }
      const frame = Math.floor(this.read / 2), fraction = this.read / 2 - frame;
      for (let c = 0; c < channels.length; c++) {
        const a = this.ring[(frame * 2 + c) % this.ring.length];
        const b = this.ring[((frame + 1) * 2 + c) % this.ring.length];
        channels[c][i] = a + (b - a) * fraction;
      }
      this.read += step * 2;
    }
    return true;
  }
}
registerProcessor('piano-stream', PianoStream);
