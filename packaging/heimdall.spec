# Packages the ALREADY-PUBLISHED output rather than building from source in
# rpmbuild. NativeAOT wants a .NET SDK and clang, which would make this spec
# drag a large BuildRequires set behind it for no benefit — CI has already done
# that work and produced a self-contained tree.
#
# Version is injected by the workflow: rpmbuild --define "version 0.1.0"
%global _build_id_links none
%global __strip /bin/true

Name:           heimdall
Version:        %{?version}%{!?version:0.0.0}
Release:        1%{?dist}
Summary:        A file manager for KDE that consumes the desktop instead of reimplementing it

License:        MIT
URL:            https://github.com/dkflint723/heimdall
Source0:        heimdall-linux-x64.tar.gz

ExclusiveArch:  x86_64

# Loaded at runtime by the bundled SkiaSharp, and by the desktop integration.
Requires:       fontconfig
Requires:       glib2
Requires:       shared-mime-info
Requires:       xdg-utils

# Each of these turns a feature on. The application starts and runs without
# them, so they are not hard requirements — see README.
Recommends:     git
Recommends:     avahi-tools

# The binary is NativeAOT: no .NET runtime, and no debuginfo worth extracting.
%global debug_package %{nil}

%description
Heimdall is a Linux-first file manager built with Avalonia. It reads the
desktop's own configuration — colour scheme, icon theme, font, trash, bookmarks
and mounts — from the same places KDE reads them, rather than maintaining a
parallel set of settings.

%prep
%setup -q -n heimdall

%install
# The whole publish directory travels together: libSkiaSharp.so and
# libHarfBuzzSharp.so are loaded from the binary's own directory, so copying out
# the executable alone produces something that aborts before it draws anything.
install -d %{buildroot}%{_libdir}/heimdall
cp -a Heimdall.Ui *.so %{buildroot}%{_libdir}/heimdall/

install -d %{buildroot}%{_bindir}
ln -s %{_libdir}/heimdall/Heimdall.Ui %{buildroot}%{_bindir}/heimdall

install -D -m644 %{_sourcedir}/heimdall.desktop \
    %{buildroot}%{_datadir}/applications/heimdall.desktop

for size in 16 24 48; do
    install -D -m644 %{_sourcedir}/icons/hicolor/${size}x${size}/apps/heimdall.svg \
        %{buildroot}%{_datadir}/icons/hicolor/${size}x${size}/apps/heimdall.svg
done
install -D -m644 %{_sourcedir}/icons/hicolor/scalable/apps/heimdall.svg \
    %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/heimdall.svg

# Refresh the desktop and icon caches. Fedora 30+ has RPM file triggers that do
# this automatically, so these are belt-and-braces there — but the spec should
# not depend on the host distribution having them, and `|| :` means a machine
# without the tools installed still upgrades cleanly.
%post
/usr/bin/update-desktop-database &>/dev/null || :
/usr/bin/touch --no-create %{_datadir}/icons/hicolor &>/dev/null || :

%postun
/usr/bin/update-desktop-database &>/dev/null || :
# $1 is the number of remaining copies: 0 means this was an uninstall rather
# than the old half of an upgrade, and only then should the cache be rebuilt.
if [ $1 -eq 0 ]; then
    /usr/bin/touch --no-create %{_datadir}/icons/hicolor &>/dev/null || :
    /usr/bin/gtk-update-icon-cache %{_datadir}/icons/hicolor &>/dev/null || :
fi

%posttrans
/usr/bin/gtk-update-icon-cache %{_datadir}/icons/hicolor &>/dev/null || :

%files
%license LICENSE
%doc README.md
%{_libdir}/heimdall/
%{_bindir}/heimdall
%{_datadir}/applications/heimdall.desktop
%{_datadir}/icons/hicolor/*/apps/heimdall.svg

%changelog
* Wed Jul 29 2026 Flint <noreply@users.noreply.github.com> - 0.1.0-1
- Initial package
