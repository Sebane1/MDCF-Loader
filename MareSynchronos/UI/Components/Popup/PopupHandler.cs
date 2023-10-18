﻿using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using ImGuiNET;
using MareSynchronos.Services.Mediator;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace MareSynchronos.UI.Components.Popup;

public class PopupHandler : WindowMediatorSubscriberBase
{
    protected bool _openPopup = false;
    private readonly HashSet<IPopupHandler> _handlers;
    private IPopupHandler? _currentHandler = null;

    public PopupHandler(ILogger<PopupHandler> logger, MareMediator mediator, IEnumerable<IPopupHandler> popupHandlers)
        : base(logger, mediator, "MarePopupHandler")
    {
        Flags = ImGuiWindowFlags.NoBringToFrontOnFocus
          | ImGuiWindowFlags.NoDecoration
          | ImGuiWindowFlags.NoInputs
          | ImGuiWindowFlags.NoSavedSettings
          | ImGuiWindowFlags.NoBackground
          | ImGuiWindowFlags.NoMove
          | ImGuiWindowFlags.NoNav
          | ImGuiWindowFlags.NoTitleBar;
        IsOpen = true;

        _handlers = popupHandlers.ToHashSet();

        Mediator.Subscribe<OpenReportPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = _handlers.OfType<ReportPopupHandler>().Single();
            ((ReportPopupHandler)_currentHandler).Open(msg);
            IsOpen = true;
        });

        Mediator.Subscribe<OpenCreateSyncshellPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = _handlers.OfType<CreateSyncshellPopupHandler>().Single();
            ((CreateSyncshellPopupHandler)_currentHandler).Open();
            IsOpen = true;
        });

        Mediator.Subscribe<OpenBanUserPopupMessage>(this, (msg) =>
        {
            _openPopup = true;
            _currentHandler = _handlers.OfType<BanUserPopupHandler>().Single();
            ((BanUserPopupHandler)_currentHandler).Open(msg);
            IsOpen = true;
        });

        Mediator.Subscribe<JoinSyncshellPopupMessage>(this, (_) =>
        {
            _openPopup = true;
            _currentHandler = _handlers.OfType<JoinSyncshellPopupHandler>().Single();
            ((JoinSyncshellPopupHandler)_currentHandler).Open();
            IsOpen = true;
        });

        Mediator.Subscribe<OpenSyncshellAdminPanelPopupMessage>(this, (msg) =>
        {
            IsOpen = true;
            _openPopup = true;
            _currentHandler = _handlers.OfType<SyncshellAdminPopupHandler>().Single();
            ((SyncshellAdminPopupHandler)_currentHandler).Open(msg.GroupInfo);
            IsOpen = true;
        });
    }

    public override void Draw()
    {
        if (_currentHandler == null) return;

        if (_openPopup)
        {
            ImGui.OpenPopup(WindowName);
            _openPopup = false;
        }

        var viewportSize = ImGui.GetWindowViewport().Size;
        ImGui.SetNextWindowSize(_currentHandler!.PopupSize);
        ImGui.SetNextWindowPos(viewportSize / 2, ImGuiCond.Always, new Vector2(0.5f));
        using var popup = ImRaii.Popup(WindowName, ImGuiWindowFlags.Modal);
        if (!popup) return;
        _currentHandler.DrawContent();
        ImGui.Separator();
        if (UiSharedService.IconTextButton(FontAwesomeIcon.Times, "Close"))
        {
            ImGui.CloseCurrentPopup();
        }
    }
}