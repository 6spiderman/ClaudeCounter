// ClaudeCounter panel widget for KDE Plasma 6.
//
// It never signs in or contacts Claude itself: it runs
// `claudecounter --status --json`, which reads what the running ClaudeCounter
// tray app last published, and shows that. Refresh asks the app to check now
// (`claudecounter --refresh`); Open launches it, or brings up its window if
// it is already running.

import QtQuick
import QtQuick.Layouts
import org.kde.plasma.plasmoid
import org.kde.plasma.core as PlasmaCore
import org.kde.plasma.components as PlasmaComponents
import org.kde.plasma.extras as PlasmaExtras
import org.kde.plasma.plasma5support as Plasma5Support
import org.kde.kirigami as Kirigami

PlasmoidItem {
    id: root

    // The last `--status --json` result, or null before the first one or
    // when the command could not be run at all.
    property var status: null
    property bool commandMissing: false

    readonly property var firstWindow: status && status.windows && status.windows.length > 0 ? status.windows[0] : null

    // The badge shows "--" once the app has stopped: its last numbers could
    // be hours old. (The popup still lists them, marked as not running.)
    readonly property var badgeWindow: status && status.running ? firstWindow : null

    // Same colours as the tray icon (BandPalette / Theme).
    function bandColor(band) {
        switch (band) {
        case "green": return "#2ea043";
        case "amber": return "#d29922";
        case "red": return "#f85149";
        default: return "#6e7681";
        }
    }

    function percent(utilization) {
        return Math.round(utilization) + "%";
    }

    function problemText() {
        if (commandMissing)
            return "ClaudeCounter could not be run. Is it installed?";
        if (!status)
            return "";
        if (!status.running)
            return "ClaudeCounter is not running.";
        switch (status.problem) {
        case "signInRequired":
        case "tokenExpired":
            return "Not signed in. Open ClaudeCounter to sign in.";
        case "rateLimited":
        case "network":
            return status.problemMessage || "Could not reach Claude. Showing the last numbers.";
        }
        if (!status.available)
            return "No usage yet.";
        if (status.stale)
            return "These numbers may be out of date.";
        return "";
    }

    function updatedText() {
        if (!status || !status.lastSuccessAt)
            return "";
        return "Updated " + Qt.formatTime(new Date(status.lastSuccessAt), Qt.locale().timeFormat(Locale.ShortFormat));
    }

    function money(value) {
        return value === undefined || value === null ? "?" : "$" + Number(value).toFixed(2);
    }

    // --- running the command line -------------------------------------------

    readonly property string statusCommand: "claudecounter --status --json"
    readonly property string refreshCommand: "claudecounter --refresh"
    readonly property string openCommand: "setsid -f claudecounter >/dev/null 2>&1"

    Plasma5Support.DataSource {
        id: executable
        engine: "executable"
        connectedSources: []

        onNewData: function (source, data) {
            disconnectSource(source); // so the same command can run again
            if (source === root.statusCommand) {
                root.readStatus(data["exit code"], data["stdout"]);
            } else if (source === root.refreshCommand) {
                followUp.restart(); // the app polls now; read the result shortly
            }
        }

        function run(command) {
            if (connectedSources.indexOf(command) < 0)
                connectSource(command);
        }
    }

    function readStatus(exitCode, stdout) {
        // 127: command not found. Exit code 1 still prints valid JSON.
        commandMissing = exitCode === 127;
        try {
            status = JSON.parse(stdout);
        } catch (e) {
            status = null;
        }
    }

    function query() { executable.run(statusCommand); }
    function refresh() { executable.run(refreshCommand); }
    function openApp() {
        executable.run(openCommand);
        root.expanded = false;
        followUp.restart();
    }

    Timer {
        // --status is cheap (it reads a small file), so a short interval is fine.
        interval: 30 * 1000
        running: true
        repeat: true
        triggeredOnStart: true
        onTriggered: root.query()
    }

    Timer {
        id: followUp
        interval: 2000
        onTriggered: root.query()
    }

    onExpandedChanged: {
        if (root.expanded)
            root.query();
    }

    Plasmoid.icon: "claudecounter"
    toolTipMainText: "ClaudeCounter"
    toolTipSubText: commandMissing ? "ClaudeCounter could not be run."
        : status ? status.line : "Loading..."

    Plasmoid.contextualActions: [
        PlasmaCore.Action {
            text: "Refresh"
            icon.name: "view-refresh"
            onTriggered: root.refresh()
        },
        PlasmaCore.Action {
            text: "Open ClaudeCounter"
            icon.name: "claudecounter"
            onTriggered: root.openApp()
        }
    ]

    // --- the panel badge ----------------------------------------------------

    compactRepresentation: MouseArea {
        id: compact

        readonly property bool vertical: Plasmoid.formFactor === PlasmaCore.Types.Vertical

        Layout.minimumWidth: vertical ? -1 : badge.implicitWidth
        Layout.minimumHeight: vertical ? badge.implicitHeight : -1
        Layout.preferredWidth: Layout.minimumWidth
        Layout.preferredHeight: Layout.minimumHeight

        hoverEnabled: true
        acceptedButtons: Qt.LeftButton | Qt.MiddleButton
        onClicked: function (mouse) {
            if (mouse.button === Qt.MiddleButton)
                root.refresh();
            else
                root.expanded = !root.expanded;
        }

        Rectangle {
            id: badge

            readonly property int edge: Math.min(compact.width, compact.height)

            anchors.centerIn: parent
            // Fills most of a horizontal panel's height; capped so it stays a
            // badge rather than a tall block when placed on the desktop.
            height: compact.vertical ? implicitHeight
                : Math.min(Math.max(edge * 0.8, Kirigami.Units.iconSizes.small), implicitHeight * 1.6)
            width: compact.vertical ? Math.max(compact.width * 0.8, Kirigami.Units.iconSizes.small)
                : implicitWidth + Math.max(0, height - implicitHeight)
            implicitHeight: label.implicitHeight + Kirigami.Units.smallSpacing * 2
            implicitWidth: label.implicitWidth + Kirigami.Units.smallSpacing * 3
            radius: Kirigami.Units.cornerRadius
            color: root.bandColor(root.badgeWindow ? root.badgeWindow.band : "gray")
            opacity: compact.containsMouse ? 0.85 : 1

            PlasmaComponents.Label {
                id: label
                anchors.centerIn: parent
                text: root.badgeWindow ? root.percent(root.badgeWindow.utilization) : "--"
                color: "white"
                font.bold: true
                fontSizeMode: Text.Fit
                minimumPixelSize: 8
            }
        }
    }

    // --- the popup ----------------------------------------------------------

    fullRepresentation: PlasmaExtras.Representation {
        id: popup
        Layout.minimumWidth: Kirigami.Units.gridUnit * 16
        Layout.preferredWidth: Kirigami.Units.gridUnit * 18
        // Tall enough for every row, so nothing is cut off at the bottom.
        Layout.minimumHeight: Math.max(Kirigami.Units.gridUnit * 14,
            popup.header.implicitHeight + popup.contentItem.implicitHeight + Kirigami.Units.gridUnit)
        Layout.preferredHeight: Layout.minimumHeight
        collapseMarginsHint: true

        header: PlasmaExtras.PlasmoidHeading {
            RowLayout {
                anchors.fill: parent
                spacing: Kirigami.Units.smallSpacing

                Kirigami.Heading {
                    Layout.fillWidth: true
                    level: 3
                    text: "Claude usage"
                    elide: Text.ElideRight
                }
                PlasmaComponents.ToolButton {
                    icon.name: "view-refresh"
                    enabled: root.status !== null && root.status.running
                    onClicked: root.refresh()
                    PlasmaComponents.ToolTip { text: "Check usage now" }
                }
                PlasmaComponents.ToolButton {
                    icon.name: "claudecounter"
                    text: root.status && root.status.running ? "Open" : "Start"
                    display: PlasmaComponents.AbstractButton.TextBesideIcon
                    onClicked: root.openApp()
                    PlasmaComponents.ToolTip { text: "Open ClaudeCounter" }
                }
            }
        }

        contentItem: ColumnLayout {
            spacing: Kirigami.Units.largeSpacing

            Repeater {
                model: root.status && root.status.windows ? root.status.windows : []

                delegate: ColumnLayout {
                    required property var modelData
                    Layout.fillWidth: true
                    Layout.leftMargin: Kirigami.Units.largeSpacing
                    Layout.rightMargin: Kirigami.Units.largeSpacing
                    spacing: Kirigami.Units.smallSpacing

                    RowLayout {
                        Layout.fillWidth: true
                        PlasmaComponents.Label {
                            Layout.fillWidth: true
                            text: modelData.label
                            elide: Text.ElideRight
                        }
                        PlasmaComponents.Label {
                            text: root.percent(modelData.utilization)
                            font.bold: true
                        }
                    }

                    Rectangle {
                        Layout.fillWidth: true
                        implicitHeight: Kirigami.Units.smallSpacing * 2
                        radius: height / 2
                        color: Qt.alpha(Kirigami.Theme.textColor, 0.15)

                        Rectangle {
                            width: parent.width * Math.max(0, Math.min(1, modelData.utilization / 100))
                            height: parent.height
                            radius: parent.radius
                            color: root.bandColor(modelData.band)
                        }
                    }

                    PlasmaComponents.Label {
                        visible: !!modelData.resetsIn
                        text: "Resets in " + modelData.resetsIn
                        opacity: 0.7
                        font: Kirigami.Theme.smallFont
                    }
                }
            }

            PlasmaComponents.Label {
                readonly property var extra: root.status ? root.status.extraUsage : null
                Layout.fillWidth: true
                Layout.leftMargin: Kirigami.Units.largeSpacing
                Layout.rightMargin: Kirigami.Units.largeSpacing
                visible: !!extra && extra.isEnabled
                text: extra ? "Extra usage: " + root.money(extra.usedCredits) + " of " + root.money(extra.monthlyLimit) : ""
                wrapMode: Text.WordWrap
            }

            PlasmaComponents.Label {
                Layout.fillWidth: true
                Layout.leftMargin: Kirigami.Units.largeSpacing
                Layout.rightMargin: Kirigami.Units.largeSpacing
                visible: text !== ""
                text: root.problemText()
                wrapMode: Text.WordWrap
                color: Kirigami.Theme.neutralTextColor
            }

            Item { Layout.fillHeight: true }

            PlasmaComponents.Label {
                Layout.fillWidth: true
                Layout.leftMargin: Kirigami.Units.largeSpacing
                Layout.rightMargin: Kirigami.Units.largeSpacing
                Layout.bottomMargin: Kirigami.Units.smallSpacing
                visible: text !== ""
                text: root.updatedText()
                opacity: 0.6
                font: Kirigami.Theme.smallFont
            }
        }
    }
}
