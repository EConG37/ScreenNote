"""One-request, offline Chinese/English translation with bundled OPUS-MT models."""
import json
import os
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")
os.environ["CT2_VERBOSE"] = "-1"


def resolve_direction(text, direction):
    if direction in ("en_zh", "zh_en"):
        return direction
    if direction != "auto":
        raise ValueError("请选择中文或英文翻译方向")
    chinese = len(re.findall(r"[\u3400-\u9fff]", text))
    english = len(re.findall(r"[A-Za-z]", text))
    return "zh_en" if chinese > 0 and chinese * 2 >= english else "en_zh"


def translate(request):
    import ctranslate2
    import sentencepiece

    text = request.get("Text", "").replace("\r\n", "\n").replace("\r", "\n")
    if not text.strip():
        raise ValueError("没有可翻译的文字，请在原文中输入或校对文字")
    if len(text) > 20_000:
        raise ValueError("每次最多翻译 20,000 个字符，请分段处理")
    direction = resolve_direction(text, request.get("Direction", "auto"))
    model = Path(__file__).resolve().parent / "translation-models" / direction
    # Native Windows loaders do not consistently accept non-ASCII installation paths.
    tokenizer = sentencepiece.SentencePieceProcessor(model_proto=(model / "sentencepiece.model").read_bytes())
    model_files = {file.name: file.read_bytes() for file in (model / "model").iterdir() if file.is_file()}
    engine = ctranslate2.Translator("bundled-model", files=model_files, device="cpu", compute_type="int8",
                                    inter_threads=1, intra_threads=4)
    del model_files
    inputs, layout = [], []
    for line in text.split("\n"):
        if not line.strip():
            layout.append((line, []))
            continue
        indent = re.match(r"^\s*", line).group()
        line_indices = []
        for sentence in re.split(r"(?<=[。！？!?])|(?<=\.)(?=\s)", line.strip()):
            tokens = tokenizer.encode(sentence.strip(), out_type=str)
            # Explicit chunks avoid the model's default silent truncation of long OCR lines.
            for offset in range(0, len(tokens), 240):
                line_indices.append(len(inputs))
                inputs.append(tokens[offset:offset + 240])
        layout.append((indent, line_indices))
    outputs = engine.translate_batch(inputs, beam_size=4, max_batch_size=8,
                                     max_input_length=256, max_decoding_length=512, replace_unknowns=True)
    translated = [tokenizer.decode(item.hypotheses[0]).replace("▁", " ").strip() for item in outputs]
    separator = " " if direction == "zh_en" else ""
    result = "\n".join(indent + separator.join(translated[index] for index in indices)
                       for indent, indices in layout)
    return {"Text": result, "Direction": direction, "Error": None}


try:
    if len(sys.argv) != 2:
        raise ValueError("翻译参数不正确")
    request = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    result = translate(request)
except Exception as error:
    result = {"Text": "", "Direction": "", "Error": "本地翻译未完成：" + str(error)}
print(json.dumps(result, ensure_ascii=False))
