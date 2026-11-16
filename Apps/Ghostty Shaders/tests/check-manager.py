"""Exercise the actual PowerShell menu against disposable configuration directories."""
from pathlib import Path
import os
import subprocess
import tempfile

project = Path(__file__).resolve().parents[1]
script = project / 'scripts/ghostty-shaders.ps1'
with tempfile.TemporaryDirectory(prefix='ghostty menu ') as root:
    folder = Path(root) / 'ghostty'
    folder.mkdir()
    config = folder / 'config'
    original = 'font-size = 14\ncustom-shader = existing.glsl\n'
    config.write_text(original)

    def run(*args, menu=None, env=None):
        return subprocess.run(['pwsh', '-NoProfile', '-File', str(script), '-ConfigRoot', root, *args],
                              input=menu, capture_output=True, text=True, timeout=20, env=env)

    for preset in ('commodore-64', 'apple-ii', 'amber-vt', 'ibm-green', 'arcade-crt', 'soft-crt'):
        result = run('-Action', 'Apply', '-Preset', preset)
        assert result.returncode == 0, result.stderr
        assert config.read_text().count('# BEGIN VIBEKIT') == 1
        assert original in config.read_text()
        if preset in ('soft-crt', 'ibm-green'):
            assert 'custom-shader-animation = false' in config.read_text()
    result = run('-Preset', 'apple-ii', menu='c\nmy-apple\np\n1\n1\na\nq\n')
    assert result.returncode == 0 and '❌' not in result.stdout, result.stdout + result.stderr
    personal = folder / 'vibekit-shaders/my-apple.glsl'
    assert personal.is_file()
    generated = folder / 'vibekit-crt.glsl'
    assert '#define ENABLE_CURVE 1' in generated.read_text()
    run('-Action', 'Apply')
    assert '#define ENABLE_CURVE 1' in generated.read_text(), 'Applied values must survive restart'
    before = personal.read_bytes()
    result = run(menu='c\nmy-apple\nq\n')
    assert '❌' in result.stdout and personal.read_bytes() == before
    # Fake GUI editor checks that a spaced filename arrives as exactly one argument.
    editor = Path(root) / 'fake editor.py'
    editor.write_text('import sys\nfrom pathlib import Path\nassert len(sys.argv)==2\np=Path(sys.argv[1]);p.write_text(p.read_text()+"\\n// editor verified\\n")\n')
    env = os.environ.copy()
    env['VISUAL'] = f'python3 "{editor}"'
    result = run('-Preset', 'apple-ii', menu='e\nedit-copy\n\na\nq\n', env=env)
    assert result.returncode == 0 and '❌' not in result.stdout, result.stdout + result.stderr
    assert '// editor verified' in generated.read_text()
    assert run('-Action', 'Disable').returncode == 0
    assert config.read_text() == original
    config.write_text(original + '# BEGIN VIBEKIT GHOSTTY SHADERS\n')
    malformed = config.read_bytes()
    assert run('-Action', 'Apply').returncode != 0
    assert config.read_bytes() == malformed
print('PASS: all presets, menu edits, persistence, duplicate protection, editor, disable and malformed config')
