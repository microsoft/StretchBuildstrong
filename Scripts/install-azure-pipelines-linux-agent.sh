#!/bin/bash

###
# Arguments:
#
# $1    URL
# $2    Personal Access Token
# $3    Pool Name
# $4    Agent Name
#
###

cd /usr/bin
mkdir agent
cd agent
wget -O pipelines-agent.tar.gz https://vstsagentpackage.azureedge.net/agent/2.154.3/vsts-agent-linux-x64-2.154.3.tar.gz
tar zxvf pipelines-agent.tar.gz
export AGENT_ALLOW_RUNASROOT=true
./config.sh --unattended --url $1 --auth pat --token $2 --pool $3 --agent $4 --once --acceptTeeEula
./run.sh --once
#sudo ./svc.sh install
#sudo ./svc.sh start