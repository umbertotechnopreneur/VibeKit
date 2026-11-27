using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private async Task ManageLibraryAsync()
    {
        if (workflowLibrary is not { } library || CurrentProject is not { } project || CurrentWorkflow is not { } flow) return;
        var manager = new LibraryDialog(this, library, project, flow);
        while (!closed && await manager.Dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (closed) return;
            if (manager.Draft.ActiveProjectId != library.ActiveProjectId && !await CanLeaveProjectAsync()) continue;
            if (!ReferenceEquals(manager.Draft, library))
            {
                if (!await CommitLibraryAsync(manager.Draft, manager.Workflow.Id))
                {
                    manager.Error.Text = workflowMessage.Text;
                    continue;
                }
            }
            else { workflowId = manager.Workflow.Id; RefreshWorkflowEditor(); }
            break;
        }
    }

    // All list changes live in this draft until the user accepts the manager.
    private sealed class LibraryDialog
    {
        private readonly MainWindow window;
        private readonly ListView projectList = List();
        private readonly ListView workflowList = List();
        private readonly StackPanel projects = Column(8);
        private readonly StackPanel workflows = Column(8);
        private readonly StackPanel form = Column(8);
        private readonly Grid choices;
        private readonly Button removeProject;
        private readonly Button removeWorkflow;
        private bool rebuilding;
        private string workflowId;

        public ContentDialog Dialog { get; }
        public TextBlock Error { get; } = Text("");
        public WorkflowLibrary Draft { get; private set; }
        private WorkflowProject Project => Draft.Projects.First(item => item.Id == Draft.ActiveProjectId);
        public Workflow Workflow => Project.Workflows.First(item => item.Id == workflowId);

        private static ListView List() => new()
        {
            MaxHeight = 164, SelectionMode = ListViewSelectionMode.Single, SingleSelectionFollowsFocus = false
        };

        internal LibraryDialog(MainWindow window, WorkflowLibrary library, WorkflowProject project, Workflow workflow)
        {
            this.window = window;
            Draft = library;
            workflowId = workflow.Id;
            var body = Column(10);
            Dialog = window.Dialog("Library", new ScrollViewer { Content = body,
                MaxHeight = Math.Max(210, window.root.ActualHeight - 215), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, "Open");
            projects.Children.Add(FieldLabel(projectList, T("Projects"), T("Choose the project whose workflows you want to see. Open switches to the selected project and workflow.")));
            projects.Children.Add(projectList);
            var projectButtons = new ActionStrip();
            projectButtons.Children.Add(Command("New project", () => EditName("New project", "", name =>
            {
                var first = new Workflow(WorkflowLibrary.NewId(), "Workflow 1", 180, []);
                var added = new WorkflowProject(WorkflowLibrary.NewId(), name, [first]);
                Draft = Draft with { Projects = [..Draft.Projects, added], ActiveProjectId = added.Id };
                workflowId = first.Id;
            })));
            projectButtons.Children.Add(Command("Edit project", () => EditName("Edit project", Project.Name,
                name => Draft = ReplaceProject(Draft, Project with { Name = name })), icon: "\uE70F"));
            removeProject = Command("Remove project", () => ConfirmRemove("Remove this project and its workflows?", Project.Name, () =>
            {
                int index = Array.IndexOf(Draft.Projects, Project);
                var remaining = Draft.Projects.Where(item => item.Id != Draft.ActiveProjectId).ToArray();
                var next = remaining[Math.Min(index, remaining.Length - 1)];
                Draft = Draft with { Projects = remaining, ActiveProjectId = next.Id };
                workflowId = next.Workflows[0].Id;
            }), icon: "\uE74D");
            projectButtons.Children.Add(removeProject);
            projects.Children.Add(projectButtons);

            workflows.Children.Add(FieldLabel(workflowList, T("Workflows"), T("Choose a sequence from this project. Open loads its steps into the workflow editor.")));
            workflows.Children.Add(workflowList);
            var workflowButtons = new ActionStrip();
            workflowButtons.Children.Add(Command("New workflow", () => EditName("New workflow", "", name =>
                Add(new(WorkflowLibrary.NewId(), name, 180, [])))));
            workflowButtons.Children.Add(Command("Edit workflow", () => EditName("Edit workflow", Workflow.Name,
                name => Draft = ReplaceProject(Draft, Project with { Workflows = Project.Workflows.Select(item =>
                    item.Id == workflowId ? item with { Name = name } : item).ToArray() })), icon: "\uE70F"));
            workflowButtons.Children.Add(Command("Duplicate", () => EditName("Duplicate", Workflow.Name + " (2)", name =>
                Add(Workflow with { Name = name, Id = WorkflowLibrary.NewId(), Steps = Workflow.Steps.Select(
                    step => step with { Id = WorkflowLibrary.NewId() }).ToArray() })), icon: "\uE8C8"));
            removeWorkflow = Command("Remove workflow", () => ConfirmRemove("Remove this workflow?", Workflow.Name, () =>
            {
                int index = Array.IndexOf(Project.Workflows, Workflow);
                var remaining = Project.Workflows.Where(item => item.Id != workflowId).ToArray();
                Draft = ReplaceProject(Draft, Project with { Workflows = remaining });
                workflowId = remaining[Math.Min(index, remaining.Length - 1)].Id;
            }), icon: "\uE74D");
            workflowButtons.Children.Add(removeWorkflow);
            workflows.Children.Add(workflowButtons);

            choices = ResponsiveColumns(520, Card(projects), Card(workflows));
            body.Children.Add(choices);
            Dialog.MinWidth = Math.Min(720, Math.Max(280, window.root.ActualWidth - 56));
            body.Children.Add(form);
            body.Children.Add(Error);
            projectList.SelectionChanged += (_, _) =>
            {
                if (rebuilding || projectList.SelectedIndex < 0) return;
                var selected = Draft.Projects[projectList.SelectedIndex];
                Draft = Draft with { ActiveProjectId = selected.Id };
                workflowId = selected.Workflows[0].Id;
                Refresh();
            };
            workflowList.SelectionChanged += (_, _) =>
            {
                if (!rebuilding && workflowList.SelectedIndex >= 0)
                    workflowId = Project.Workflows[workflowList.SelectedIndex].Id;
            };
            Refresh();
        }

        private void Add(Workflow flow)
        {
            Draft = ReplaceProject(Draft, Project with { Workflows = [..Project.Workflows, flow] });
            workflowId = flow.Id;
        }

        private string T(string key) => window.localization.Text(key);

        private Button Command(string label, Action action, string? icon = null)
        {
            var button = new Button { Content = icon is null ? T(label) : new FontIcon { Glyph = icon, FontSize = 16 } };
            if (icon is not null)
            {
                AutomationProperties.SetName(button, T(label));
            }
            Help(button, T(ActionDescriptions[label]));
            button.Click += (_, _) => action();
            return button;
        }

        private void Refresh()
        {
            rebuilding = true;
            projectList.ItemsSource = Draft.Projects.Select(item => item.Name).ToArray();
            projectList.SelectedIndex = Array.IndexOf(Draft.Projects, Project);
            workflowList.ItemsSource = Project.Workflows.Select(item => item.Name).ToArray();
            workflowList.SelectedIndex = Array.IndexOf(Project.Workflows, Workflow);
            removeProject.IsEnabled = Draft.Projects.Length > 1;
            removeWorkflow.IsEnabled = Project.Workflows.Length > 1;
            rebuilding = false;
        }

        private void EditName(string label, string initial, Action<string> commit)
        {
            var field = new TextBox { MaxLength = 100, Text = initial.Length <= 100 ? initial : initial[..100] };
            form.Children.Add(FieldLabel(field, T(label), T(ActionDescriptions[label])));
            form.Children.Add(field);
            var save = EditActions("Save", () => commit(field.Text.Trim()));
            field.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
            save.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
            field.Focus(FocusState.Programmatic);
            field.SelectAll();
        }

        private void ConfirmRemove(string question, string name, Action commit)
        {
            form.Children.Add(Heading(T(question)));
            form.Children.Add(Text(name));
            EditActions("Remove", commit);
        }

        private Button EditActions(string confirm, Action commit)
        {
            choices.Visibility = Visibility.Collapsed;
            Dialog.IsPrimaryButtonEnabled = false;
            Dialog.DefaultButton = ContentDialogButton.None;
            var row = Row();
            var accept = Command(confirm, () =>
            {
                commit();
                Dialog.PrimaryButtonText = T("Save changes");
                EndEdit();
            });
            row.Children.Add(accept);
            row.Children.Add(Command("Cancel", EndEdit));
            form.Children.Add(row);
            accept.StartBringIntoView();
            return accept;
        }

        private void EndEdit()
        {
            form.Children.Clear();
            choices.Visibility = Visibility.Visible;
            Dialog.IsPrimaryButtonEnabled = true;
            Dialog.DefaultButton = ContentDialogButton.Primary;
            Refresh();
        }
    }
}
