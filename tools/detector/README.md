# Note Detector

Standalone Python app that turns your guitar signal into note events for the plugin.

```powershell
python -m venv .venv
.venv\Scripts\activate
pip install -r requirements.txt

python detector.py --list-devices          # find your NUX input
python detector.py --device NUX            # stream notes to 127.0.0.1:4242
```

Make sure the NUX Mighty Space USB routing is set to **Dry Out** (clean signal)
- distortion/reverb in the signal chain will degrade detection accuracy.
