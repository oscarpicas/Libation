﻿using DataLayer;
using Dinah.Core;
using Dinah.Core.ErrorHandling;
using Dinah.Core.Windows.Forms;
using FileLiberator;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LibationWinForms.BookLiberation
{
	// decouple serilog and form. include convenience factory method
	public class LogMe
	{
		public event EventHandler<string> LogInfo;
		public event EventHandler<string> LogErrorString;
		public event EventHandler<(Exception, string)> LogError;

		private LogMe()
		{
			LogInfo += (_, text) => Serilog.Log.Logger.Information($"Automated backup: {text}");
			LogErrorString += (_, text) => Serilog.Log.Logger.Error(text);
			LogError += (_, tuple) => Serilog.Log.Logger.Error(tuple.Item1, tuple.Item2 ?? "Automated backup: error");
		}

		public static LogMe RegisterForm(AutomatedBackupsForm form = null)
		{
			var logMe = new LogMe();

			if (form is null)
				return logMe;

			logMe.LogInfo += (_, text) => form?.WriteLine(text);

			logMe.LogErrorString += (_, text) => form?.WriteLine(text);

			logMe.LogError += (_, tuple) =>
			{
				form?.WriteLine(tuple.Item2 ?? "Automated backup: error");
				form?.WriteLine("ERROR: " + tuple.Item1.Message);
			};

			return logMe;
		}

		public void Info(string text) => LogInfo?.Invoke(this, text);
		public void Error(string text) => LogErrorString?.Invoke(this, text);
		public void Error(Exception ex, string text = null) => LogError?.Invoke(this, (ex, text));
	}

	public static class ProcessorAutomationController
	{
		public static async Task BackupSingleBookAsync(LibraryBook libraryBook, EventHandler<LibraryBook> completedAction = null)
		{
			Serilog.Log.Logger.Information("Begin backup single {@DebugInfo}", new { libraryBook?.Book?.AudibleProductId });

			LogMe logMe = LogMe.RegisterForm();
			var backupBook = CreateBackupBook(completedAction, logMe);

			// continue even if libraryBook is null. we'll display even that in the processing box
			await new BackupSingle(logMe, backupBook, libraryBook).RunBackupAsync();
		}

		public static async Task BackupAllBooksAsync(EventHandler<LibraryBook> completedAction = null)
		{
			Serilog.Log.Logger.Information("Begin " + nameof(BackupAllBooksAsync));

			var automatedBackupsForm = new AutomatedBackupsForm();
			LogMe logMe = LogMe.RegisterForm(automatedBackupsForm);

			var backupBook = CreateBackupBook(completedAction, logMe);

			await new BackupLoop(logMe, backupBook, automatedBackupsForm).RunBackupAsync();

		}

		public static async Task ConvertAllBooksAsync()
		{
			Serilog.Log.Logger.Information("Begin " + nameof(ConvertAllBooksAsync));

			var automatedBackupsForm = new AutomatedBackupsForm();
			LogMe logMe = LogMe.RegisterForm(automatedBackupsForm);

			var convertBook = CreateStreamableProcessable<ConvertToMp3, AudioConvertForm>(null, logMe);

			await new BackupLoop(logMe, convertBook, automatedBackupsForm).RunBackupAsync();
		}

		private static BackupBook CreateBackupBook(EventHandler<LibraryBook> completedAction, LogMe logMe)
		{
			var downloadPdf = CreateStreamableProcessable<DownloadPdf, DownloadForm>(completedAction, logMe);
			var downloadDecryptBook = CreateStreamableProcessable<DownloadDecryptBook, AudioDecryptForm>(completedAction, logMe);
			return new BackupBook(downloadDecryptBook, downloadPdf);
		}

		public static async Task BackupAllPdfsAsync(EventHandler<LibraryBook> completedAction = null)
		{
			Serilog.Log.Logger.Information("Begin " + nameof(BackupAllPdfsAsync));

			var automatedBackupsForm = new AutomatedBackupsForm();
			LogMe logMe = LogMe.RegisterForm(automatedBackupsForm);

			var downloadPdf = CreateStreamableProcessable<DownloadPdf, DownloadForm>(completedAction, logMe);

			await new BackupLoop(logMe, downloadPdf, automatedBackupsForm).RunBackupAsync();
		}

		public static void DownloadFile(string url, string destination, bool showDownloadCompletedDialog = false)
		{
			new System.Threading.Thread(() =>
			{
				(DownloadFile downloadFile, DownloadForm downloadForm) = CreateStreamable<DownloadFile, DownloadForm>();

				if (showDownloadCompletedDialog)
					downloadFile.StreamingCompleted += (_, __) => MessageBox.Show("File downloaded");

				downloadFile.PerformDownloadFileAsync(url, destination).GetAwaiter().GetResult();
			})
			{ IsBackground = true }
			.Start();
		}

		/// <summary>
		/// Create a new <see cref="IStreamProcessable"/> and which creates a new <see cref="ProcessBaseForm"/> on IProcessable.Begin.
		/// </summary>
		/// <typeparam name="TStrProc">The <see cref="IStreamProcessable"/> derrived type to create.</typeparam>
		/// <typeparam name="TForm">The <see cref="ProcessBaseForm"/> derrived form to create on Begin</typeparam>
		/// <param name="completedAction">An additional event handler to handle <typeparamref name="TStrProc"/>.Completed</param>
		/// <returns>A new <see cref="IStreamProcessable"/> of type <typeparamref name="TStrProc"/></returns>
		private static TStrProc CreateStreamableProcessable<TStrProc, TForm>(EventHandler<LibraryBook> completedAction = null, LogMe logMe = null)
			where TForm : ProcessBaseForm, new()
			where TStrProc : IStreamProcessable, new()
		{
			var strProc = new TStrProc();

			strProc.Begin += (sender, libraryBook) =>
			{
				var processForm = new TForm();
				processForm.SetProcessable(strProc, logMe.Info);
				processForm.OnBegin(sender, libraryBook);
			};

			if (completedAction != null)
				strProc.Completed += completedAction;

			return strProc;
		}
		private static (TStrProc, TForm) CreateStreamable<TStrProc, TForm>(EventHandler<LibraryBook> completedAction = null)
			where TForm : StreamBaseForm, new()
			where TStrProc : IStreamable, new()
		{
			var strProc = new TStrProc();

			var streamForm = new TForm();
			streamForm.SetStreamable(strProc);

			return (strProc, streamForm);
		}
	}

	internal abstract class BackupRunner
	{
		protected LogMe LogMe { get; }
		protected IProcessable Processable { get; }
		protected AutomatedBackupsForm AutomatedBackupsForm { get; }

		protected BackupRunner(LogMe logMe, IProcessable processable, AutomatedBackupsForm automatedBackupsForm = null)
		{
			LogMe = logMe;
			Processable = processable;
			AutomatedBackupsForm = automatedBackupsForm;
		}

		protected abstract Task RunAsync();

		protected abstract string SkipDialogText { get; }
		protected abstract MessageBoxButtons SkipDialogButtons { get; }
		protected abstract DialogResult CreateSkipFileResult { get; }

		public async Task RunBackupAsync()
		{
			AutomatedBackupsForm?.Show();

			try
			{
				await RunAsync();
			}
			catch (Exception ex)
			{
				LogMe.Error(ex);
			}

			AutomatedBackupsForm?.FinalizeUI();
			LogMe.Info("DONE");
		}

		protected async Task<bool> ProcessOneAsync(Func<LibraryBook, Task<StatusHandler>> func, LibraryBook libraryBook)
		{
			string logMessage;

			try
			{
				var statusHandler = await func(libraryBook);

				if (statusHandler.IsSuccess)
					return true;

				foreach (var errorMessage in statusHandler.Errors)
					LogMe.Error(errorMessage);

				logMessage = statusHandler.Errors.Aggregate((a, b) => $"{a}\r\n{b}");
			}
			catch (Exception ex)
			{
				LogMe.Error(ex);

				logMessage = ex.Message + "\r\n|\r\n" + ex.StackTrace;
			}

			LogMe.Error("ERROR. All books have not been processed. Most recent book: processing failed");

			string details;
			try
			{
				static string trunc(string str)
					=> string.IsNullOrWhiteSpace(str) ? "[empty]"
					: (str.Length > 50) ? $"{str.Truncate(47)}..."
					: str;

				details =
$@"  Title: {libraryBook.Book.Title}
  ID: {libraryBook.Book.AudibleProductId}
  Author: {trunc(libraryBook.Book.AuthorNames)}
  Narr: {trunc(libraryBook.Book.NarratorNames)}";
			}
			catch
			{
				details = "[Error retrieving details]";
			}

			var dialogResult = MessageBox.Show(string.Format(SkipDialogText, details), "Skip importing this book?", SkipDialogButtons, MessageBoxIcon.Question);

			if (dialogResult == DialogResult.Abort)
				return false;

			if (dialogResult == CreateSkipFileResult)
			{
				ApplicationServices.LibraryCommands.UpdateBook(libraryBook, LiberatedStatus.Error, null);
				var path = FileManager.AudibleFileStorage.Audio.CreateSkipFile(libraryBook.Book.Title, libraryBook.Book.AudibleProductId, logMessage);
				LogMe.Info($@"
Created new 'skip' file
  [{libraryBook.Book.AudibleProductId}] {libraryBook.Book.Title}
  {path}
".Trim());
			}

			return true;
		}
	}

	internal class BackupSingle : BackupRunner
	{
		private LibraryBook _libraryBook { get; }

		protected override string SkipDialogText => @"
An error occurred while trying to process this book. Skip this book permanently?
{0}

- Click YES to skip this book permanently.

- Click NO to skip the book this time only. We'll try again later.
".Trim();
		protected override MessageBoxButtons SkipDialogButtons => MessageBoxButtons.YesNo;
		protected override DialogResult CreateSkipFileResult => DialogResult.Yes;

		public BackupSingle(LogMe logMe, IProcessable processable, LibraryBook libraryBook)
			: base(logMe, processable)
		{
			_libraryBook = libraryBook;
		}

		protected override async Task RunAsync()
		{
			if (_libraryBook is not null)
				await ProcessOneAsync(Processable.ProcessSingleAsync, _libraryBook);
		}
	}

	internal class BackupLoop : BackupRunner
	{
		protected override string SkipDialogText => @"
An error occurred while trying to process this book.
{0}

- ABORT: stop processing books.

- RETRY: retry this book later. Just skip it for now. Continue processing books. (Will try this book again later.)

- IGNORE: Permanently ignore this book. Continue processing books. (Will not try this book again later.)
".Trim();
		protected override MessageBoxButtons SkipDialogButtons => MessageBoxButtons.AbortRetryIgnore;
		protected override DialogResult CreateSkipFileResult => DialogResult.Ignore;

		public BackupLoop(LogMe logMe, IProcessable processable, AutomatedBackupsForm automatedBackupsForm)
			: base(logMe, processable, automatedBackupsForm) { }

		protected override async Task RunAsync()
		{
			// support for 'skip this time only' requires state. iterators provide this state for free. therefore: use foreach/iterator here
			foreach (var libraryBook in Processable.GetValidLibraryBooks())
			{
				var keepGoing = await ProcessOneAsync(Processable.ProcessBookAsync_NoValidation, libraryBook);
				if (!keepGoing)
					return;

				if (AutomatedBackupsForm.IsDisposed)
					break;

				if (!AutomatedBackupsForm.KeepGoing)
				{
					if (!AutomatedBackupsForm.KeepGoingChecked)
						LogMe.Info("'Keep going' is unchecked");
					return;
				}
			}

			LogMe.Info("Done. All books have been processed");
		}
	}
}
