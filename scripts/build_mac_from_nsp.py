#!/usr/bin/env python3
"""Package the Mac app for one game from the build folder made by build_ipa.py.

The Mac host (dm/host/dmhost) and the app bundle are rebuilt; the patched modules, Mac shader
cache, profile and shared-font stand-ins are copied from <game folder>/<title ID>_build. The
game's RomFS stays outside the app; data-dir.txt in the bundle points at the build folder.

Usage: python3 scripts/build_mac_from_nsp.py "<game folder or .nsp/.xci>" [--game ID] [--out-dir DIR]
       [--app-name NAME] [--icon PATH] [--save save] [--lang en-US]
       [--skip-host] [--no-zip]
"""
import argparse, os, plistlib, re, shutil, subprocess, sys
from pathlib import Path

# Settings for F5 in Spyder; command-line options override them.
NSP_DIR = ''
GAME = ''
OUT_DIR = ''
APP_NAME = 'Rhythm Heaven Groove'
ICON = ''
SAVE = 'save'
GAME_LANG = 'en-US'
SKIP_HOST = False
NO_ZIP = False

REPO = Path(__file__).resolve().parent.parent
DM = REPO / 'dm'


def die(msg):
    print(f'[RED] {msg}', file=sys.stderr)
    sys.exit(1)


def find_build(nsp: Path, game: str):
    if game:
        build = nsp / f'{game}_build'
        if (build / f'profile/{game}/game.tsv').exists():
            return game, build
        die(f'no build for {game} in {nsp} ({build}/profile/{game}/game.tsv missing)')
    found = []
    for d in nsp.glob('*_build'):
        for p in d.glob('profile/*/game.tsv'):
            found.append((p.parent.name, d))
    if len(found) != 1:
        die(f'expected one <title ID>_build/profile/<title ID>/game.tsv in {nsp}, found {len(found)}; pass --game ID')
    return found[0]


def make_icon(src: Path, out: Path):
    if src.suffix.lower() == '.icns':
        shutil.copy2(src, out); return True
    if not src.is_file(): return False
    tmp = out.parent / 'icon.iconset'
    shutil.rmtree(tmp, ignore_errors=True); tmp.mkdir()
    try:
        for size in (16, 32, 64, 128, 256, 512, 1024):
            for scale, suffix in ((1, ''), (2, '@2x')):
                n = size * scale
                if n > 1024: continue
                png = tmp / f'icon_{size}x{size}{suffix}.png'
                subprocess.run(['sips', '-s', 'format', 'png', '-z', str(n), str(n), str(src), '--out', str(png)],
                               check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        subprocess.run(['iconutil', '-c', 'icns', str(tmp), '-o', str(out)], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return True
    except (OSError, subprocess.CalledProcessError) as e:
        print(f'[YELLOW] cannot build the app icon from {src}: {e}', file=sys.stderr)
        return False
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('nsp_dir', nargs='?', help='game folder, or a .nsp / .xci file (default: NSP_DIR setting, else asked)')
    ap.add_argument('--game', help='base game title ID (default: the only <title ID>_build in the NSP folder)')
    ap.add_argument('--out-dir', help='where the .app and README are written (default: <build>/mac-app)')
    ap.add_argument('--app-name', help='app name (default: APP_NAME setting)')
    ap.add_argument('--icon', help='.icns/.png/.jpg app icon (default: the existing app icon, else the game icon)')
    ap.add_argument('--save', help='save folder name under Application Support (default: save)')
    ap.add_argument('--lang', help='game language written to DM_LANG (default: en-US)')
    ap.add_argument('--skip-host', action='store_true', help='use the existing dm/host/dmhost instead of rebuilding it')
    ap.add_argument('--no-zip', action='store_true', help='do not create the .zip next to the mac-app folder')
    a = ap.parse_args()

    nsp = Path(os.path.expanduser(a.nsp_dir or NSP_DIR or input('NSP folder or base .xci: '))).resolve()
    if nsp.is_file():
        if nsp.suffix.lower() not in ('.nsp', '.xci'): die(f'not a folder or a .nsp/.xci file: {nsp}')
        nsp = nsp.parent   # the build folder is beside the base XCI
    if not nsp.is_dir(): die(f'not a folder: {nsp}')
    game, build = find_build(nsp, a.game or GAME)
    profile = build / 'profile' / game
    patched = build / 'patched_v0'
    romfs = build / 'extract/v0/RomFS'
    cache = build / 'shader/cache'
    font = build / 'font'
    for p, what in ((profile / 'game.tsv', 'profile'), (romfs, 'RomFS'), (cache, 'Mac shader cache'),
                    (patched / 'main.patched.bin', 'patched main')):
        if not p.exists(): die(f'{what} not found: {p}\n  run scripts/build_ipa.py on this folder, a base .nsp or a base .xci first (the IPA build creates it)')
    app_name = a.app_name or APP_NAME
    out_dir = Path(os.path.expanduser(a.out_dir or OUT_DIR or str(build / 'mac-app'))).resolve()
    save_name = a.save or SAVE
    lang = a.lang or GAME_LANG
    if not re.fullmatch(r'[A-Za-z0-9_.-]+', lang): die(f'invalid language: {lang}')

    # 1. Mac host, built against the profile outside the repo
    if not (a.skip_host or SKIP_HOST):
        print(f'[mac] building dmhost for {game} ...')
        env = dict(os.environ, DM_GAMES_DIR=str(build / 'profile'))
        r = subprocess.run(['make', '-C', str(DM / 'host'), f'GAME={game}', 'dmhost'], env=env)
        if r.returncode: die('dmhost build failed')
    host = DM / 'host/dmhost'
    if not host.is_file(): die(f'no dmhost at {host}')

    # 2. Fonts: the build folder usually has the Mac stand-ins; create them if missing
    if not font.is_dir():
        font.mkdir(parents=True)
        std = '/System/Library/Fonts/Supplemental/Arial Unicode.ttf'
        if Path(std).is_file(): shutil.copy2(std, font / 'standard.ttf')
        ext = DM / 'ios/Gen/dm/font/nintendo_ext.ttf'
        if ext.is_file(): shutil.copy2(ext, font / 'nintendo_ext.ttf')

    # 3. App bundle
    old_icon = out_dir / f'{app_name}.app/Contents/Resources/AppIcon.icns'
    saved_icon = out_dir / 'AppIcon.icns'
    if old_icon.is_file(): shutil.copy2(old_icon, saved_icon)
    app = out_dir / f'{app_name}.app'
    shutil.rmtree(app, ignore_errors=True)
    (app / 'Contents/MacOS').mkdir(parents=True)
    (app / 'Contents/Resources').mkdir(parents=True)
    res = app / 'Contents/Resources'

    shutil.copy2(host, res / 'dmhost')
    shutil.copytree(patched, res / 'patched_v0')
    shutil.copytree(cache, res / 'shader_cache')
    shutil.copytree(profile, res / 'profile')
    if font.is_dir(): shutil.copytree(font, res / 'font')
    (res / 'data-dir.txt').write_text(str(build) + '\n')

    icon = a.icon or ICON
    if icon:
        make_icon(Path(os.path.expanduser(icon)), res / 'AppIcon.icns')
    elif saved_icon.is_file():
        shutil.copy2(saved_icon, res / 'AppIcon.icns')
    else:
        game_icon = build / 'extract/v0/icon.jpg'
        if game_icon.is_file(): make_icon(game_icon, res / 'AppIcon.icns')

    exe = 'RhythmHeavenGroove'
    launcher = app / 'Contents/MacOS' / exe
    launcher.write_text(f'''#!/bin/sh
set -e
APP="$(cd "$(dirname "$0")/../Resources" && pwd)"
DATA="$(cat "$APP/data-dir.txt" 2>/dev/null || true)"
[ -n "$DATA" ] || DATA="{build}"
ROOT="$HOME/Library/Application Support/RhythmHeavenGroove"
mkdir -p "$ROOT/{save_name}" "$ROOT/temp"
LOG="$ROOT/log.txt"
export DM_ROOT="$ROOT"
export DM_ROMFS="$DATA/extract/v0/RomFS"
export DM_SAVE="$ROOT/{save_name}"
export DM_TEMP="$ROOT/temp"
export DM_MSL_DIR="$APP/shader_cache"
export DM_NVN_SHMAP="$APP/profile/shader_map.tsv"
export DM_FONT_DIR="$APP/font"
export DM_LANG={lang}
export DM_AUDIO=1
export DM_OPMODE=0
export DM_OVERLAY=1
export DM_NVN_BC7_UPLOAD=1
export DM_WINDOW=1
export DM_PRESENT_LINK=0
exec "$APP/dmhost" "$APP/patched_v0" >"$LOG" 2>&1
''')
    launcher.chmod(0o755)

    info = {
        'CFBundleDevelopmentRegion': 'en', 'CFBundleDisplayName': app_name, 'CFBundleExecutable': exe,
        'CFBundleIdentifier': f'com.nxnative.{game.lower()}.mac', 'CFBundleInfoDictionaryVersion': '6.0',
        'CFBundleName': app_name, 'CFBundlePackageType': 'APPL', 'CFBundleShortVersionString': '0.1',
        'CFBundleVersion': '0.1', 'LSMinimumSystemVersion': '13.0', 'NSHighResolutionCapable': True,
        'LSApplicationCategoryType': 'public.app-category.games',
    }
    if (res / 'AppIcon.icns').is_file(): info['CFBundleIconFile'] = 'AppIcon'
    with open(app / 'Contents/Info.plist', 'wb') as f: plistlib.dump(info, f)

    (out_dir / 'README.txt').write_text(f'''{app_name} — Mac test build (nxnative)
================================================

Double-click {app_name}.app (right-click → Open the first time; the build is unsigned).

The app contains the host, the patched game modules, the shader cache, the profile and the
fonts. The game's RomFS is not copied: Contents/Resources/data-dir.txt points at

  {build}

If that folder moves, edit data-dir.txt.

Controls: WASD left stick, arrows D-pad, J=A K=B U=X I=Y, Q=L E=R Z=ZL C=ZR,
F=press left stick, G=press right stick, Return=+, Tab=-, mouse=touch.

Log and save:
  ~/Library/Application Support/RhythmHeavenGroove/log.txt
  ~/Library/Application Support/RhythmHeavenGroove/{save_name}/

This is an unsigned local test build.
''')

    # 4. Zip
    if not (a.no_zip or NO_ZIP):
        slug = re.sub(r'[^A-Za-z0-9]+', '', app_name) or 'MacApp'
        zip_path = out_dir.parent / f'{slug}-mac.zip'
        if zip_path.exists(): zip_path.unlink()
        subprocess.run(['ditto', '-c', '-k', '--norsrc', '--keepParent', str(out_dir), str(zip_path)], check=True)
        print(f'[mac] IPA-like package: {zip_path}')

    print(f'[mac] done: {app}')
    print(f'[mac] run: open "{app}"')


if __name__ == '__main__':
    main()
