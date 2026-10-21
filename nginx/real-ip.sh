#!/bin/sh
# Prints the nginx directives that recover the visitor's address from X-Forwarded-For,
# trusting that header only when the request arrives from a CIDR listed in
# OPENRESTO_TRUSTED_PROXIES (comma separated). Prints nothing when it is empty.
set -e
set -f

found=""
for cidr in $(printf '%s' "${OPENRESTO_TRUSTED_PROXIES:-}" | tr ',' ' '); do
  case "$cidr" in
    *[!0-9a-fA-F:./]*)
      echo "OPENRESTO_TRUSTED_PROXIES: '$cidr' is not an IP address or CIDR range." >&2
      exit 1
      ;;
  esac
  echo "    set_real_ip_from $cidr;"
  found=1
done

if [ -n "$found" ]; then
  echo "    real_ip_header X-Forwarded-For;"
  echo "    real_ip_recursive on;"
fi
