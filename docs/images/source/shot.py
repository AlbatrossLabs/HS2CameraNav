import pathlib
from playwright.sync_api import sync_playwright

# Renders banner.html into docs/images. Same pipeline as HS2CameraRoutes/docs/images/source/shot.py.
#   banner.png        1600 x 400 at 2x  README banner; also fits itch's page header (min 960 x 340)
#   itch-cover.png     630 x 500 at 2x  itch.io cover (recommended 630 x 500, max 3 MB)
#   youtube-thumb.jpg 1280 x 720 at 1x  YouTube thumbnail (1280 x 720, max 2 MB)
#   discord.png       1200 x 800 at 2x  Discord forum post (3:2)
here = pathlib.Path(__file__).parent.resolve()
jobs = [
    ('banner',  (1600, 400), 2, 'banner.png'),
    ('itch',    (630, 500),  2, 'itch-cover.png'),
    ('youtube', (1280, 720), 1, 'youtube-thumb.jpg'),
    ('discord', (1200, 800), 2, 'discord.png'),
]
with sync_playwright() as p:
    chrome = pathlib.Path('/usr/bin/google-chrome')
    b = p.chromium.launch(executable_path=str(chrome)) if chrome.exists() else p.chromium.launch()
    for name, (w, h), scale, out in jobs:
        pg = b.new_page(viewport={'width': w, 'height': h}, device_scale_factor=scale)
        pg.goto(f'file://{here}/banner.html?layout={name}')
        pg.wait_for_timeout(500)
        path = here.parent / out
        if out.endswith('.jpg'):
            pg.locator('.stage').screenshot(path=str(path), type='jpeg', quality=92)
        else:
            pg.locator('.stage').screenshot(path=str(path))
        print(f'{out}: {path.stat().st_size / 1e6:.2f} MB')
    b.close()
