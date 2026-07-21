#!/bin/bash
npm run assets:build && dotnet build && systemctl restart premierapi && journalctl -u premierapi -f
