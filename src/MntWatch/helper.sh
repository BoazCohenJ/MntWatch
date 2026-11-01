#!/bin/sh
# MntWatch helper. MntWatch.exe starts this as root inside the WSL distro and drives it over stdin, one
# tab-separated command per line. It only uses POSIX sh, coreutils/busybox, awk and mount, so it runs on
# Ubuntu, Debian, Alpine and friends without installing anything.
#
#   MOUNT  <target> <windows path> <options>   overmount a Windows folder with inode caching
#   UMOUNT <target>
#   N      <path>                              queue a path to nudge
#   F      <seq>                               nudge everything queued, then answer ACK <seq> <count>
#   SWEEP  <target> <seq>                      queue every path under target (after a lost event), then flush
#   STAT   <path>                              answer STAT <size> as Linux currently sees it (self-test)
#   PING   <x>                                 answer PONG <x>
#
# When stdin closes (MntWatch exited or was killed) every folder this helper mounted is unmounted again.
rm -f "$0"
VERSION=1
T=$(printf '\t')
B=$(mktemp /tmp/mntwatch.XXXXXX) || exit 1
MOUNTS="$B.mounts"
: > "$MOUNTS"

say() { printf '%s\n' "$*"; }

cleanup() {
  while IFS= read -r m; do
    umount "$m" 2>/dev/null || umount -l "$m" 2>/dev/null
  done < "$MOUNTS"
  rm -f "$B" "$B".*
}
trap 'cleanup; exit 0' HUP INT TERM

# Writing each path's own mode back makes the kernel fetch fresh attributes from Windows (size, mtime) and
# raise IN_ATTRIB for it, which file watchers report as a change. Nothing on the Windows side changes.
# Symlinks are skipped because chmod follows them and would change the target. Paths that no longer exist
# make stat fail, which still drops the stale directory entry.
flush() {
  n=0
  if [ -s "$B" ]; then
    n=$(wc -l < "$B")
    tr '\n' '\0' < "$B" | xargs -0 -r stat -c '%f %a %n' 2>/dev/null | grep -v '^a[0-9a-f][0-9a-f][0-9a-f] ' > "$B.m"
    for mode in $(cut -d' ' -f2 "$B.m" | sort -u); do
      grep "^[0-9a-f]* $mode " "$B.m" | cut -d' ' -f3- | tr '\n' '\0' | xargs -0 -r chmod "$mode" 2>/dev/null
    done
    : > "$B"
  fi
  say "ACK${T}$1${T}$n"
}

do_mount() {
  if out=$(mount -t drvfs "$2" "$1" -o "$3" 2>&1); then
    printf '%s\n' "$1" >> "$MOUNTS"
    say "OK${T}MOUNT${T}$1"
  else
    say "ERR${T}MOUNT${T}$1${T}$(printf '%s' "$out" | tr '\t\n' '  ')"
  fi
}

do_umount() {
  how=""
  if ! umount "$1" 2>/dev/null; then
    # Something still has its working directory inside; detach now, the kernel finishes once it's free.
    umount -l "$1" 2>/dev/null && how="${T}lazy"
  fi
  grep -vxF "$1" "$MOUNTS" > "$MOUNTS.new"
  mv "$MOUNTS.new" "$MOUNTS"
  say "OK${T}UMOUNT${T}$1$how"
}

say "HELLO${T}$VERSION${T}$(uname -r)"
awk -v t="$T" '$4 ~ /aname=drvfs/ || $3 == "drvfs" { print "DRVFS" t $2 t $4 }' /proc/mounts
say "READY"

while IFS= read -r line; do
  cmd=${line%%"$T"*}
  rest=${line#*"$T"}
  case "$cmd" in
    N) printf '%s\n' "$rest" >> "$B" ;;
    F) flush "$rest" ;;
    MOUNT)
      target=${rest%%"$T"*}; rest=${rest#*"$T"}
      win=${rest%%"$T"*}; opts=${rest#*"$T"}
      do_mount "$target" "$win" "$opts" ;;
    UMOUNT) do_umount "$rest" ;;
    SWEEP)
      target=${rest%%"$T"*}; seq=${rest#*"$T"}
      find "$target" -xdev -print >> "$B" 2>/dev/null
      flush "$seq" ;;
    STAT) say "STAT${T}$(stat -c %s "$rest" 2>/dev/null || echo -1)" ;;
    PING) say "PONG${T}$rest" ;;
  esac
done
cleanup
