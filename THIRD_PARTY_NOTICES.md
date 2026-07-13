# Third-party notices

LocalTranslation includes or adapts the following open-source components.

## Meetily Parakeet engine

- Project: [Zackriya-Solutions/meetily](https://github.com/Zackriya-Solutions/meetily)
- Copyright: © 2024 Zackriya Solutions
- License: MIT

The in-process Parakeet TDT decoding flow in `MeetilyParakeetRecognizer.cs` is
adapted from Meetily's Rust implementation. The MIT license text is available
in the upstream repository and permits use, modification, and redistribution
with attribution.

## NVIDIA Parakeet TDT 0.6B V3

- Model: [nvidia/parakeet-tdt-0.6b-v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3)
- ONNX conversion: [istupakov/parakeet-tdt-0.6b-v3-onnx](https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx)
- License: CC BY 4.0

The model supports English and 24 additional European languages. It does not
support Chinese or Japanese speech recognition; use SenseVoice or Whisper for
those source languages.
