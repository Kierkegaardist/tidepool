# Kokoro TTS for Tidepool's read-aloud (Tts.cs keeps one of these running). One JSON line in: {"text", "voice", "speed"};
# one line out: a base64 16-bit mono WAV, or "ERR <message>". Model + voices sit next to this file.
import base64, io, json, os, sys, wave
import numpy as np
from kokoro_onnx import Kokoro

sys.stdin.reconfigure(encoding="utf-8-sig")   # the pipe would be cp1252; -sig drops a BOM some writers put first
here = os.path.dirname(os.path.abspath(__file__))
k = Kokoro(os.path.join(here, "kokoro-v1.0.onnx"), os.path.join(here, "voices-v1.0.bin"))
k.create("Ready.", voice="af_heart")   # warm-up: the first call is slow
print("READY", flush=True)

for line in sys.stdin:
    try:
        m = json.loads(line)
        v = m.get("voice") or "af_heart"
        samples, rate = k.create(m["text"], voice=v, speed=float(m.get("speed") or 1.0), lang="en-gb" if v[0] == "b" else "en-us")
        buf = io.BytesIO()
        with wave.open(buf, "wb") as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
            w.writeframes((np.clip(samples, -1, 1) * 32767).astype("<i2").tobytes())
        print(base64.b64encode(buf.getvalue()).decode(), flush=True)
    except Exception as e:
        print("ERR " + str(e).replace("\n", " "), flush=True)
