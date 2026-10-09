"""Independent boundary checks against the pinned official CommonMark example corpus.

The corpus (CommonMark 0.31.2, John MacFarlane, CC-BY-SA 4.0) is downloaded only
to the chosen external test cache. It is not shipped in the plugin or repository.
Requires markdown-it-py as an independent grammar oracle, and the test executable
built by build-translation.ps1. This is an explicit extended check, not an LLM test.
"""

import argparse
import bisect
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request

SPEC_URL = 'https://spec.commonmark.org/0.31.2/spec.json'
SPEC_SHA256 = 'd431b29d97b6f73e69d547109cf5081578fac931e72afe95639ebe766c1b2a20'


def main():
    args = argparse.ArgumentParser(description=__doc__)
    args.add_argument('--test-executable', type=Path, required=True)
    args.add_argument('--cache-root', type=Path, required=True)
    options = args.parse_args()
    from markdown_it import MarkdownIt
    root = options.cache_root.resolve()
    root.mkdir(parents=True, exist_ok=True)
    spec_path = root / 'commonmark-0.31.2.json'
    data = spec_path.read_bytes() if spec_path.exists() else urllib.request.urlopen(SPEC_URL, timeout=30).read()
    if hashlib.sha256(data).hexdigest() != SPEC_SHA256:
        raise RuntimeError('CommonMark corpus hash does not match the pinned version')
    if not spec_path.exists():
        spec_path.write_bytes(data)
    examples = json.loads(data.decode('utf-8-sig'))
    cases = []
    for example in examples:
        source = example['markdown']
        before = '\n\n'.join('Independent paragraph ' + str(i) + ' ' + 'x' * 191 for i in range(4))
        after = '\n\n'.join('Following paragraph ' + str(i) + ' ' + 'y' * 213 for i in range(4))
        for variant, text in [('raw', source), ('surrounded', before + '\n\n' + source + '\n\n' + after)]:
            cases.append(dict(id=str(example['example']) + '-' + variant, source=text, section=example['section']))
    case_path = root / 'spec-cases.json'
    result_path = root / 'spec-results.json'
    case_path.write_text(json.dumps(cases, ensure_ascii=False), encoding='utf-8')
    process = subprocess.run([str(options.test_executable.resolve()), '--spec-cases', str(case_path), str(result_path)], check=False)
    if process.returncode not in (0, 1) or not result_path.exists():
        raise RuntimeError('Corpus test executable did not produce a report')
    results = json.loads(result_path.read_text(encoding='utf-8-sig'))
    if len(results) != len(cases):
        raise RuntimeError('Corpus report has an incorrect number of records')
    parser = MarkdownIt('commonmark')
    errors = []
    plans = cuts = 0
    for case, result in zip(cases, results):
        if case['id'] != result['id']:
            raise RuntimeError('Corpus report does not match input IDs')
        if 'error' in result:
            errors.append(dict(id=case['id'], error=result['error']))
            continue
        text = case['source']
        # The C# offsets are UTF-16 code units. The oracle's token maps are line-based.
        starts = [0]
        units = 0
        for character in text:
            units += len(character.encode('utf-16-le')) // 2
            if character == '\n':
                starts.append(units)
        blocks = [t for t in parser.parse(text) if t.level == 0 and t.map and t.nesting != -1]
        for plan in result['plans']:
            plans += 1
            for offset in plan['cuts']:
                cuts += 1
                line = bisect.bisect_right(starts, offset) - 1
                if offset != starts[line]:
                    errors.append(dict(id=case['id'], error='cut_inside_line', offset=offset))
                crossed = [t.type for t in blocks if t.map[0] < line < t.map[1]]
                if crossed:
                    errors.append(dict(id=case['id'], error='cut_inside_commonmark_block', line=line, types=crossed))
    report = dict(examples=len(examples), documents=len(cases), plans=plans, observed_cuts=cuts,
                  errors=errors, spec=SPEC_URL, sha256=SPEC_SHA256)
    report_path = root / 'spec-report.json'
    report_path.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print('CommonMark: examples=%d documents=%d plans=%d cuts=%d errors=%d' %
          (len(examples), len(cases), plans, cuts, len(errors)))
    print('Report:', report_path)
    return 1 if errors or process.returncode else 0


if __name__ == '__main__':
    raise SystemExit(main())
