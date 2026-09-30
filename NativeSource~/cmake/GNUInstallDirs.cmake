# Minimal GNUInstallDirs shim (Phase 0).
# CMake 4.2.2's stock GNUInstallDirs recurses infinitely in
# _GNUInstallDirs_RUNSTATEDIR_get_default when included by Blend2D. We do not install
# these fetched deps, so a lean shim that defines the standard variables is sufficient.
# Placed first on CMAKE_MODULE_PATH so include(GNUInstallDirs) resolves here.

set(CMAKE_INSTALL_BINDIR       "bin"                                CACHE PATH "user executables")
set(CMAKE_INSTALL_SBINDIR      "sbin"                               CACHE PATH "system admin executables")
set(CMAKE_INSTALL_LIBEXECDIR   "libexec"                            CACHE PATH "program executables")
set(CMAKE_INSTALL_SYSCONFDIR   "etc"                                CACHE PATH "read-only single-machine data")
set(CMAKE_INSTALL_LOCALSTATEDIR "var"                              CACHE PATH "modifiable single-machine data")
set(CMAKE_INSTALL_RUNSTATEDIR  "run"                                CACHE PATH "run-time variable data")
set(CMAKE_INSTALL_LIBDIR       "lib"                                CACHE PATH "object code libraries")
set(CMAKE_INSTALL_INCLUDEDIR   "include"                            CACHE PATH "C header files")
set(CMAKE_INSTALL_DATAROOTDIR  "share"                              CACHE PATH "read-only arch-independent data root")
set(CMAKE_INSTALL_DATADIR      "${CMAKE_INSTALL_DATAROOTDIR}"       CACHE PATH "read-only arch-independent data")
set(CMAKE_INSTALL_MANDIR       "${CMAKE_INSTALL_DATAROOTDIR}/man"   CACHE PATH "man documentation")
set(CMAKE_INSTALL_DOCDIR       "${CMAKE_INSTALL_DATAROOTDIR}/doc"   CACHE PATH "documentation root")

# Full paths some consumers reference.
foreach(dir BIN SBIN LIBEXEC SYSCONF LOCALSTATE RUNSTATE LIB INCLUDE DATAROOT DATA MAN DOC)
  set(CMAKE_INSTALL_FULL_${dir}DIR "${CMAKE_INSTALL_PREFIX}/${CMAKE_INSTALL_${dir}DIR}")
endforeach()
