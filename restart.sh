#!/bin/bash
dotnet build && systemctl restart premierapi && journalctl -u premierapi -f
