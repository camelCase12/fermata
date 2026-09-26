#!/usr/bin/env python3
"""Generates a synthetic music library for trying Fermata, screenshots and tests.

Every artist, album and song is fictional, and every track is a short chord. The library
has FLAC with a PICTURE block, MP3 with ID3v2.3 and ID3v2.4, AAC and ALAC in M4A, Opus and
Vorbis with Base64 METADATA_BLOCK_PICTURE, WavPack with APEv2, WAV with RIFF INFO, AIFF with
an ID3 chunk, WMA and Matroska audio. It also has folder art where a format cannot embed
pictures, a two-disc album, a various-artists compilation, featured artists, loose singles,
synchronized .lrc lyrics and embedded unsynchronized lyrics.

Usage: tools/make-sample-library.py OUTPUT_DIR [--seconds MIN MAX]
Requires ffmpeg with libmp3lame, libopus, libvorbis and the gradients/drawtext filters.
"""
import argparse
import base64
import random
import shutil
import struct
import subprocess
import sys
from pathlib import Path

FONT = subprocess.run(['fc-match', '-f', '%{file}', 'sans:bold'], capture_output=True, text=True).stdout.strip()

ALBUMS = [
    # artist, album, year, genre, format, palette, track titles
    ('Aurora Vale', 'Tidal Hours', 2021, 'Dream Pop', 'flac', ('0x1d2b53', '0xff77a8'),
     ['Low Tide', 'Salt in the Air', 'Harbor Lights', 'Undertow', 'The Long Shore', 'Moonwater', 'Driftwood Heart', 'Beacon']),
    ('Aurora Vale', 'Glass Garden', 2018, 'Dream Pop', 'mp3-v24', ('0x0b3d2e', '0x9be564'),
     ['Greenhouse', 'Fern Light', 'Petals', 'Conservatory', 'Bloom Slowly', 'Wilt']),
    ('The Northern Arcs', 'Signal Fires', 2016, 'Indie Rock', 'mp3-v23', ('0x401010', '0xffb347'),
     ['Kindling', 'Signal Fires', 'Smoke Letters', 'Across the Ridge', 'Ember Song', 'Last Watch', 'Ash & Iron', 'Dawn Patrol', 'Homeward']),
    ('The Northern Arcs', 'Cartography', 2019, 'Indie Rock', 'm4a-aac', ('0x13293d', '0xe8c547'),
     ['Legend', 'Contour Lines', 'True North', 'Scale 1:50000', 'Uncharted', 'Compass Rose', 'Here Be Dragons']),
    ('Kenji Morrow', 'Night Transit', 2022, 'Electronic', 'opus', ('0x0d0221', '0x2de2e6'),
     ['Platform 9', 'Neon Rain', 'Last Train Home', 'Overpass', 'City Sleeps', 'Terminal', 'Red Line']),
    ('Kenji Morrow', 'Circuit Bloom', 2020, 'Electronic', 'ogg', ('0x1a1a2e', '0xe94560'),
     ['Boot Sequence', 'Capacitor', 'Solder Flowers', 'Logic Gate', 'Oscillate', 'Power Down']),
    ('Marisol Ortega', 'Canción del Río', 2015, 'Latin', 'flac', ('0x5c2a0e', '0xf4d35e'),
     ['Agua Clara', 'Orilla', 'La Corriente', 'Puente Viejo', 'Río Abajo', 'Canción del Río']),
    ('Oskar Lindqvist Trio', 'Blue Hours', 2012, 'Jazz', 'm4a-alac', ('0x0f2027', '0x2c5364'),
     ['Blue Hour', 'Walking Bass', 'Late Set', 'Brushes', 'Minor Detour', 'Coda for Anna']),
    ('Halcyon Static', 'Weather Systems', 2023, 'Ambient', 'wv', ('0x2b2d42', '0x8d99ae'),
     ['Front', 'Isobar', 'Pressure Drop', 'Squall Line', 'Clearing']),
    ('Beatrix Kowalczyk', 'Paper Crowns', 2017, 'Singer-Songwriter', 'mp3-v24', ('0x6d2e46', '0xd5b9b2'),
     ['Paper Crowns', 'Kitchen Radio', 'Letters I Never Sent', 'Small Town Parade', 'Heirloom', 'Goodnight, Rosie']),
    ('Grey Meridian', 'Iron Sky', 2010, 'Metal', 'aiff', ('0x111111', '0xb22222'),
     ['Forge', 'Iron Sky', 'Hammerfall', 'Molten', 'Anvil Choir']),
    ('Sofia Brandt', 'Nocturnes for a Quiet House', 2014, 'Classical', 'wav', ('0x2e1f27', '0xc9ada7'),
     ['Nocturne No. 1', 'Nocturne No. 2', 'Nocturne No. 3', 'Interlude', 'Nocturne No. 4']),
    ('DJ Parallax', 'Club Geometry', 2021, 'House', 'wma', ('0x240046', '0xff9e00'),
     ['Tessellate', 'Vertex', 'Hypotenuse', 'Fractal Floor']),
    ('Sable & Finch', 'Lanterns', 2020, 'Folk', 'mka', ('0x3a2e39', '0xf7b267'),
     ['Lantern Walk', 'Field Notes', 'Hollow Oak', 'Firefly', 'Morning Chores']),
]

DOUBLE_ALBUM = ('Aurora Vale', 'Constellations', 2024, 'Dream Pop', 'flac', ('0x020024', '0x00d4ff'),
                [['Polaris', 'Cassiopeia', 'Orion Rising', 'Lyra', 'Draco'],
                 ['Andromeda', 'Vega', 'Pleiades', 'Cygnus', 'Ursa Minor']])

COMPILATION = ('Various Artists', 'Summer Sessions Vol. 3', 2022, 'Pop', 'mp3-v24', ('0xff6b6b', '0xfeca57'),
               [('Aurora Vale', 'Heatwave'), ('Kenji Morrow feat. Aurora Vale', 'Coastline'),
                ('The Northern Arcs', 'August'), ('Marisol Ortega', 'Verano'), ('Beatrix Kowalczyk', 'Lemonade Stand'),
                ('Sable & Finch', 'Porch Swing')])

SINGLES = [('Kenji Morrow feat. Marisol Ortega', 'Luz de Neón', 2023, 'Electronic'),
           ('Halcyon Static', 'Test Pattern', 2024, 'Ambient'),
           ('Grey Meridian', 'Rust (Demo)', 2009, 'Metal')]

LYRICS = ['Where the water meets the light', 'I keep a lantern burning bright', 'Every tide that pulls away',
          'Brings you back another day', 'Hold the harbor, hold the line', 'Salt and silver, yours and mine',
          'When the moon forgets its name', 'I will call you all the same']


def run(args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"command failed: {' '.join(map(str, args))}\n{result.stderr[-2000:]}")


# A third colour for each cover's pattern, chosen by the cover's seed.
ACCENTS = ['0xfff1d0', '0x7afcff', '0xff6b6b', '0xffd166', '0xa0e7e5', '0xf15bb5', '0xcaffbf', '0xffffff']

# Pattern masks for ffmpeg's geq filter, from 0 to 255, on a 600-pixel square, with edges
# softened over a pixel or two.
PATTERNS = [
    # A low sun cut by horizontal stripes.
    "255*clip(150-hypot(X-300,Y-250),0,1)*if(lt(Y,250),1,clip(mod(Y,26)-9,0,1))",
    # Concentric rings off to one side.
    "255*clip((sin(hypot(X-420,Y-180)/13)-0.25)*4,0,1)*clip(270-hypot(X-420,Y-180),0,1)",
    # Rolling waves over the lower half.
    "255*clip((sin((Y+45*sin(X/65))/17)-0.4)*4,0,1)*clip((Y-260)/40,0,1)",
    # Diagonal stripes inside a circle.
    "255*clip(mod(X+Y,90)-58,0,1)*clip(230-hypot(X-300,Y-270),0,1)",
    # A grid of dots that grow towards the bottom.
    "255*clip(6+22*Y/600-hypot(mod(X,75)-37,mod(Y,75)-37),0,1)",
]


def make_cover(path, title, artist, colors, seed):
    c0, c1 = colors
    accent = ACCENTS[seed % len(ACCENTS)]
    pattern = PATTERNS[seed % len(PATTERNS)]
    text = title.replace("'", "’").replace(':', '\\:')
    by = artist.replace("'", "’").replace(':', '\\:')
    graph = (f"[1]format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{pattern}'[shape];"
             f"[0][shape]overlay,"
             f"drawtext=fontfile='{FONT}':text='{text}':fontcolor=white@0.92:fontsize=46:x=40:y=h-130"
             f":shadowcolor=black@0.4:shadowx=2:shadowy=2,"
             f"drawtext=fontfile='{FONT}':text='{by}':fontcolor=white@0.75:fontsize=28:x=40:y=h-72"
             f":shadowcolor=black@0.4:shadowx=2:shadowy=2")
    run(['ffmpeg', '-loglevel', 'error', '-y',
         '-f', 'lavfi', '-i', f'gradients=s=600x600:c0={c0}:c1={c1}:x0=0:y0=0:x1=600:y1=600:seed={seed}:duration=1',
         '-f', 'lavfi', '-i', f'color=c={accent}:s=600x600:d=1',
         '-filter_complex', graph, '-frames:v', '1', '-q:v', '3', str(path)])


def picture_block(image_path):
    """A FLAC PICTURE block, Base64 encoded for METADATA_BLOCK_PICTURE."""
    data = image_path.read_bytes()
    mime = b'image/jpeg'
    block = struct.pack('>II', 3, len(mime)) + mime + struct.pack('>I', 0) + struct.pack('>IIIII', 600, 600, 24, 0, len(data)) + data
    return base64.b64encode(block).decode('ascii')


def chord(seconds, root):
    """A soft major chord with fades, as an ffmpeg lavfi expression."""
    freqs = [root, root * 5 / 4, root * 3 / 2]
    parts = '+'.join(f'0.08*sin(2*PI*{f:.2f}*t)' for f in freqs)
    fade = f'min(1,t/1.5)*min(1,({seconds}-t)/2)'
    return f"aevalsrc='({parts})*{fade}|({parts})*{fade}':s=44100:d={seconds}"


def encode(out, fmt, seconds, root, tags, cover=None, lyrics=None):
    args = ['ffmpeg', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', chord(seconds, root)]
    embed = cover is not None and fmt in ('flac', 'mp3-v23', 'mp3-v24', 'm4a-aac', 'm4a-alac', 'wma', 'mka')
    if embed:
        args += ['-i', str(cover), '-map', '0:a', '-map', '1:v', '-c:v', 'copy', '-disposition:v', 'attached_pic',
                 '-metadata:s:v', 'title=Album cover', '-metadata:s:v', 'comment=Cover (front)']
    codec = {
        'flac': ['-c:a', 'flac'],
        'mp3-v23': ['-c:a', 'libmp3lame', '-q:a', '6', '-id3v2_version', '3'],
        'mp3-v24': ['-c:a', 'libmp3lame', '-q:a', '6', '-id3v2_version', '4'],
        'm4a-aac': ['-c:a', 'aac', '-b:a', '96k'],
        'm4a-alac': ['-c:a', 'alac'],
        'opus': ['-c:a', 'libopus', '-b:a', '64k'],
        'ogg': ['-c:a', 'libvorbis', '-q:a', '2'],
        'wv': ['-c:a', 'wavpack'],
        'wav': ['-c:a', 'pcm_s16le'],
        'aiff': ['-c:a', 'pcm_s16be', '-write_id3v2', '1'],
        'wma': ['-c:a', 'wmav2', '-b:a', '96k'],
        'mka': ['-c:a', 'libopus', '-b:a', '64k'],
    }[fmt]
    args += codec
    for key, value in tags.items():
        args += ['-metadata', f'{key}={value}']
    if lyrics and fmt.startswith('mp3'):
        args += ['-metadata', f'lyrics={lyrics}']
    if cover is not None and fmt in ('opus', 'ogg'):
        args += ['-metadata', f'METADATA_BLOCK_PICTURE={picture_block(cover)}']
    args.append(str(out))
    run(args)


EXTENSIONS = {'flac': 'flac', 'mp3-v23': 'mp3', 'mp3-v24': 'mp3', 'm4a-aac': 'm4a', 'm4a-alac': 'm4a', 'opus': 'opus',
              'ogg': 'ogg', 'wv': 'wv', 'wav': 'wav', 'aiff': 'aiff', 'wma': 'wma', 'mka': 'mka'}


def safe(name):
    return ''.join('_' if c in '/\\:*?"<>|' else c for c in name)


def write_lrc(path, seconds):
    step = max(3.0, (seconds - 4) / len(LYRICS))
    lines = []
    for i, text in enumerate(LYRICS):
        t = 2 + i * step
        lines.append(f'[{int(t // 60):02d}:{t % 60:05.2f}]{text}')
    path.write_text('[ar:Fermata sample]\n[ti:Sample lyrics]\n' + '\n'.join(lines) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('output', type=Path)
    parser.add_argument('--seconds', type=float, nargs=2, default=(24, 60), metavar=('MIN', 'MAX'))
    args = parser.parse_args()
    if shutil.which('ffmpeg') is None:
        sys.exit('ffmpeg is required')
    rng = random.Random(1234)
    out = args.output
    out.mkdir(parents=True, exist_ok=True)
    roots = [196.0, 220.0, 246.9, 261.6, 293.7, 329.6, 349.2]
    count = 0

    def length():
        return round(rng.uniform(*args.seconds), 1)

    for index, (artist, album, year, genre, fmt, colors, titles) in enumerate(ALBUMS):
        folder = out / safe(artist) / f'{year} - {safe(album)}'
        folder.mkdir(parents=True, exist_ok=True)
        cover = folder / 'cover.jpg'
        make_cover(cover, album, artist, colors, index + 1)
        for number, title in enumerate(titles, 1):
            seconds = length()
            featured = f'{artist} feat. Sable & Finch' if (album == 'Tidal Hours' and number == 4) else artist
            tags = {'title': title, 'artist': featured, 'album_artist': artist, 'album': album, 'date': str(year),
                    'genre': genre, 'track': f'{number}/{len(titles)}'}
            if fmt == 'wv':
                tags['albumartist'] = artist
            target = folder / f'{number:02d} - {safe(title)}.{EXTENSIONS[fmt]}'
            lyrics = '\n'.join(LYRICS[:4]) if (fmt == 'mp3-v24' and number == 1) else None
            encode(target, fmt, seconds, rng.choice(roots), tags, cover, lyrics)
            if album == 'Tidal Hours' and number in (1, 3):
                write_lrc(target.with_suffix('.lrc'), seconds)
            count += 1
        # Formats that embed pictures do not need the folder image; keep it for the others.
        if fmt in ('flac', 'mp3-v23', 'mp3-v24', 'm4a-aac', 'm4a-alac', 'wma', 'mka', 'opus', 'ogg') and index % 3 != 0:
            cover.unlink()

    artist, album, year, genre, fmt, colors, discs = DOUBLE_ALBUM
    folder = out / safe(artist) / f'{year} - {safe(album)}'
    cover = out / 'constellations-cover.jpg'
    make_cover(cover, album, artist, colors, 99)
    for disc, titles in enumerate(discs, 1):
        disc_folder = folder / f'CD{disc}'
        disc_folder.mkdir(parents=True, exist_ok=True)
        shutil.copy(cover, disc_folder / 'folder.jpg')
        for number, title in enumerate(titles, 1):
            tags = {'title': title, 'artist': artist, 'album_artist': artist, 'album': album, 'date': str(year),
                    'genre': genre, 'track': f'{number}/{len(titles)}', 'disc': f'{disc}/{len(discs)}'}
            encode(disc_folder / f'{number:02d} - {safe(title)}.flac', fmt, length(), rng.choice(roots), tags)
            count += 1
    cover.unlink()

    artist, album, year, genre, fmt, colors, tracks = COMPILATION
    folder = out / 'Compilations' / safe(album)
    folder.mkdir(parents=True, exist_ok=True)
    cover = folder / 'cover.jpg'
    make_cover(cover, album, 'Various Artists', colors, 42)
    for number, (track_artist, title) in enumerate(tracks, 1):
        tags = {'title': title, 'artist': track_artist, 'album_artist': 'Various Artists', 'album': album,
                'date': str(year), 'genre': genre, 'track': f'{number}/{len(tracks)}', 'compilation': '1'}
        encode(folder / f'{number:02d} - {safe(title)}.mp3', fmt, length(), rng.choice(roots), tags, cover)
        count += 1

    singles = out / 'Singles'
    singles.mkdir(exist_ok=True)
    for artist, title, year, genre in SINGLES:
        tags = {'title': title, 'artist': artist, 'date': str(year), 'genre': genre}
        encode(singles / f'{safe(artist)} - {safe(title)}.mp3', 'mp3-v24', length(), rng.choice(roots), tags)
        count += 1
    # An untagged file, which the library names after the file.
    run(['ffmpeg', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', chord(20, 220.0), '-c:a', 'libmp3lame',
         '-q:a', '7', '-map_metadata', '-1', '-write_xing', '1', str(singles / 'Unknown Artist - Field Recording 7.mp3')])
    count += 1
    print(f'Wrote {count} tracks to {out}')


if __name__ == '__main__':
    main()
