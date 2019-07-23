
# Stretch Buildstrong

Azure Pipelines Hosted Build servers are pretty awesome. You don't need to manage your own build VM's, you get a fresh VM for every build, and you have near infinite scale. However, if you need your builds to connect to on-premise servers behind a firewall, you need software installed that's not part of the Microsoft image, or you want to use a bigger beefier VM size....well you're out of luck. This project is trying to bring the hosted build benefits - specifically elastic scale, and fresh VM per build - to private build servers, that live on a VNet of your choice, use a VM image of your choice, and can be a VM size of your choice.
