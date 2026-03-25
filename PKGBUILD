# Maintainer: strong-ery <https://github.com/strong-ery>
pkgname=stramp
pkgver=0.1.0
pkgrel=1
pkgdesc="Strong's music player — compact GTK4 shuffle player for your local library"
arch=('any')
url="https://github.com/strong-ery/stramp"
license=('CC-BY-NC-SA-4.0')
depends=(
    'python'
    'python-gobject'
    'gtk4'
    'libadwaita'
    'mpv'
    'python-mpv'
    'python-mutagen'
)
makedepends=(
    'python-hatchling'
    'python-build'
    'python-installer'
)
source=("$pkgname-$pkgver.tar.gz::$url/archive/refs/tags/v$pkgver.tar.gz")
sha256sums=('SKIP')  # Replace with real checksum before AUR submission

build() {
    cd "$pkgname-$pkgver"
    python -m build --wheel --no-isolation
}

package() {
    cd "$pkgname-$pkgver"
    python -m installer --destdir="$pkgdir" dist/*.whl

    # Desktop entry
    install -Dm644 stramp.desktop "$pkgdir/usr/share/applications/stramp.desktop"

    # License
    install -Dm644 LICENSE "$pkgdir/usr/share/licenses/$pkgname/LICENSE"
}
