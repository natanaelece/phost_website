#!/bin/bash
npm run css:build && dotnet build && systemctl restart premierapi && journalctl -u premierapi -f
