# dmgbuild settings for tools/mac-dmg.sh: NeoBuzz Live.app beside a link to Applications, on the drawn
# background (make-background.py; the positions below match it), with the read-me.
# dmgbuild passes -D values in `defines`.
import os.path

application = defines["app"]  # noqa: F821 (dmgbuild provides it)
readme = defines["readme"]  # noqa: F821
here = defines["here"]  # noqa: F821

format = "UDZO"
filesystem = "HFS+"
files = [application, readme]
symlinks = {"Applications": "/Applications"}
# No hide_extensions: it writes Finder info onto the bundle, which `codesign --strict` rejects as
# detritus; Finder hides an app's .app by itself.

volume_icon = os.path.join(application, "Contents", "Resources", "AppIcon.icns")
if os.path.isfile(volume_icon):
    icon = volume_icon

background = os.path.join(here, "background.png")  # with background@2x.png beside it
window_rect = ((200, 120), (640, 480))
default_view = "icon-view"
show_status_bar = False
show_tab_view = False
show_toolbar = False
show_pathbar = False
show_sidebar = False
icon_size = 80
text_size = 12
arrange_by = None
icon_locations = {
    os.path.basename(application): (170, 190),   # NeoBuzz Live.app
    "Applications": (470, 190),
    os.path.basename(readme): (556, 372),
}
