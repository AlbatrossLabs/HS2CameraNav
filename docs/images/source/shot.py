import pathlib
from playwright.sync_api import sync_playwright

# Renders banner.html at 2x into docs/images: discord.png (1200 x 800, 3:2).
# Same pipeline as HS2CameraRoutes/docs/images/source/shot.py.
here = pathlib.Path(__file__).parent.resolve()
sizes = {'discord': (1200, 800)}
with sync_playwright() as p:
    chrome = pathlib.Path('/usr/bin/google-chrome')
    b = p.chromium.launch(executable_path=str(chrome)) if chrome.exists() else p.chromium.launch()
    for name, (w, h) in sizes.items():
        pg = b.new_page(viewport={'width': w, 'height': h}, device_scale_factor=2)
        pg.goto(f'file://{here}/banner.html?layout={name}')
        pg.wait_for_timeout(500)
        out = here.parent / {'discord': 'discord.png'}[name]
        pg.locator('.stage').screenshot(path=str(out))
    b.close()
